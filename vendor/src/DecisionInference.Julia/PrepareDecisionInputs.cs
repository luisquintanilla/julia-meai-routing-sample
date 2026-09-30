using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using Tokenizers.HuggingFace.Tokenizer;

namespace DecisionInference.Julia;

/// <summary>Owned buffers, exposed through standard read-only tensor spans.</summary>
public sealed class JuliaInputBatch
{
    public DecisionInput Input { get; }
    public int BatchSize => Input.Questions.Count;
    public int SequenceLength { get; }
    public int MarkerWidth { get; }
    internal long[] Ids { get; }
    internal long[] Attention { get; }
    internal long[] Positions { get; }
    internal bool[] Masks { get; }
    internal long[] Types { get; }
    internal int[] OptionCounts { get; }
    public ReadOnlyTensorSpan<long> InputIds => new(Ids, [BatchSize, SequenceLength]);
    public ReadOnlyTensorSpan<long> AttentionMask => new(Attention, [BatchSize, SequenceLength]);
    public ReadOnlyTensorSpan<long> MarkerPositions => new(Positions, [BatchSize, MarkerWidth]);
    public ReadOnlyTensorSpan<bool> MarkerMask => new(Masks, [BatchSize, MarkerWidth]);
    public ReadOnlyTensorSpan<long> QuestionTypes => new(Types, [BatchSize]);

    internal JuliaInputBatch(DecisionInput input, int length, int width, long[] ids, long[] attention,
        long[] positions, bool[] masks, long[] types, int[] counts)
        => (Input, SequenceLength, MarkerWidth, Ids, Attention, Positions, Masks, Types, OptionCounts) =
            (input, length, width, ids, attention, positions, masks, types, counts);
}

/// <summary>
/// Julia's Python strict sequence/Collator recipe: 1024 context, 256 head, 48 tokens per option.
/// Owns its native tokenizer; preparation and disposal are serialized.
/// </summary>
public sealed class PrepareDecisionInputs : IDisposable
{
    public const int MaxLength = 1024;
    public const int HeadLength = 256;
    private readonly Tokenizer _tokenizer;
    private readonly string[] _reserved;
    private readonly HashSet<long> _reservedIds;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private bool _disposed;

    public PrepareDecisionInputs(string tokenizerJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var root = document.RootElement;
        var model = root.GetProperty("model");
        var pre = root.GetProperty("pre_tokenizer");
        var normalizer = root.GetProperty("normalizer");
        if (root.GetProperty("truncation").ValueKind != JsonValueKind.Null ||
            root.GetProperty("padding").ValueKind != JsonValueKind.Null ||
            model.GetProperty("type").GetString() != "BPE" || !model.GetProperty("byte_fallback").GetBoolean() ||
            !model.GetProperty("fuse_unk").GetBoolean() || model.GetProperty("ignore_merges").GetBoolean() ||
            model.GetProperty("unk_token").GetString() != "<unk>" ||
            model.GetProperty("dropout").ValueKind != JsonValueKind.Null ||
            model.GetProperty("continuing_subword_prefix").ValueKind != JsonValueKind.Null ||
            model.GetProperty("end_of_word_suffix").ValueKind != JsonValueKind.Null ||
            normalizer.GetProperty("type").GetString() != "Replace" ||
            normalizer.GetProperty("pattern").GetProperty("String").GetString() != " " ||
            normalizer.GetProperty("content").GetString() != "\u2581" ||
            pre.GetProperty("type").GetString() != "Metaspace" ||
            pre.GetProperty("replacement").GetString() != "\u2581" ||
            pre.GetProperty("prepend_scheme").GetString() != "always" || !pre.GetProperty("split").GetBoolean())
            throw new NotSupportedException("Expected Julia's lossless Replace/Metaspace/ByteFallback BPE configuration.");
        var added = root.GetProperty("added_tokens").EnumerateArray().ToArray();
        string[] framing = ["<pad>", "<eos>", "<bos>", "<unk>", "<mask>"];
        for (int i = 0; i < framing.Length; i++)
        {
            var token = added.Single(t => t.GetProperty("content").GetString() == framing[i]);
            if (!token.GetProperty("special").GetBoolean() || token.GetProperty("id").GetInt32() != i ||
                model.GetProperty("vocab").GetProperty(framing[i]).GetInt32() != i)
                throw new NotSupportedException("Julia framing token metadata disagrees.");
        }
        var special = added.Where(t => t.GetProperty("special").GetBoolean()).ToArray();
        _reserved = special.Select(t => t.GetProperty("content").GetString()!).ToArray();
        _reservedIds = special.Select(t => t.GetProperty("id").GetInt64()).ToHashSet();
        _tokenizer = Tokenizer.FromFile(tokenizerJsonPath);
    }

    public JuliaInputBatch Prepare(DecisionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string[][] labels = input.Questions.Select(Labels).ToArray();
            var state = Encode(PythonJson.RenderState(input.State), MaxLength, "state", cancellationToken);
            var rows = new List<(long[] Ids, long[] Positions, long Type)>();
            for (int row = 0; row < input.Questions.Count; row++)
            {
                var q = input.Questions[row];
                var options = labels[row].Select(x => Encode(" " + x, 48, "option", cancellationToken)).ToArray();
                int budget = HeadLength - options.Sum(x => x.Length + 1);
                if (budget < 16) throw new ArgumentException("Julia options exceed the lossless head budget.");
                var head = Encode($"{KindName(q.Kind)} question: {q.Instructions}", budget, "instructions", cancellationToken);
                int room = MaxLength - (head.Length + options.Sum(x => x.Length + 1) + 4);
                if (room < 1 || state.Length > room) throw new ArgumentException("Julia state exceeds the lossless context budget.");
                List<long> ids = [2, .. head, 1];
                List<long> positions = [];
                foreach (var option in options) { positions.Add(ids.Count); ids.Add(4); ids.AddRange(option); }
                ids.Add(1);
                ids.AddRange(state);
                ids.Add(1);
                rows.Add((ids.ToArray(), positions.ToArray(), q.Kind switch { DecisionKind.Choice => 0, DecisionKind.Score => 1, _ => 2 }));
            }
            int length = Math.Min(MaxLength, ((rows.Max(r => r.Ids.Length) + 7) / 8) * 8);
            int width = rows.Max(r => r.Positions.Length);
            var idsBuffer = new long[checked(rows.Count * length)];
            var attention = new long[idsBuffer.Length];
            var positionsBuffer = new long[checked(rows.Count * width)];
            var masks = new bool[positionsBuffer.Length];
            for (int row = 0; row < rows.Count; row++)
            {
                rows[row].Ids.CopyTo(idsBuffer, row * length);
                Array.Fill(attention, 1L, row * length, rows[row].Ids.Length);
                rows[row].Positions.CopyTo(positionsBuffer, row * width);
                Array.Fill(masks, true, row * width, rows[row].Positions.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(input, length, width, idsBuffer, attention, positionsBuffer, masks,
                rows.Select(r => r.Type).ToArray(), labels.Select(x => x.Length).ToArray());
        }
        finally { _gate.Release(); }
    }

    private long[] Encode(string text, int budget, string part, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StrictUtf8.GetByteCount(text);
        if (_reserved.Any(t => text.Contains(t, StringComparison.Ordinal)))
            throw new NotSupportedException($"Reserved Julia token in {part}.");
        var ids = _tokenizer.Encode(text, addSpecialTokens: false).Single().Ids.Select(i => (long)i).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Length > budget) throw new ArgumentException($"Julia {part} token budget exceeded; truncation is disabled.");
        if (ids.Any(_reservedIds.Contains)) throw new NotSupportedException("Tokenizer emitted a reserved Julia ID from caller text.");
        return ids;
    }

    private static string[] Labels(DecisionQuestion question)
    {
        string[] labels = question switch
        {
            ChoiceDecisionQuestion c => c.Candidates.Select(c => c.Description).ToArray(),
            ScoreDecisionQuestion s => s.Rubric.ToArray(),
            BinaryDecisionQuestion b when (b.TrueDescription is null) != (b.FalseDescription is null)
                => throw new NotSupportedException("Julia Binary criteria require both descriptions or neither."),
            BinaryDecisionQuestion b => [b.FalseDescription ?? "false", b.TrueDescription ?? "true"],
            _ => throw new NotSupportedException("Unknown decision kind.")
        };
        if (labels.Length is < 2 or > 20) throw new ArgumentException("Julia requires 2-20 alternatives; routing is not implicit.");
        return labels;
    }

    private static string KindName(DecisionKind kind) => kind switch
    {
        DecisionKind.Choice => "choice", DecisionKind.Score => "score", DecisionKind.Binary => "noul",
        _ => throw new NotSupportedException()
    };

    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; _tokenizer.Dispose(); } }
        finally { _gate.Release(); }
    }
}

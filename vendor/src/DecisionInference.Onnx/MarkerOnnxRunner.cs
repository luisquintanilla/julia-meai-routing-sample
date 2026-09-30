using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;
using TensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace DecisionInference.Onnx;

internal readonly record struct MarkerInputs(int Rows, int Length, int Width,
    long[] Ids, long[] Attention, long[] Positions, bool[] Masks, long[] Types);

internal readonly record struct MarkerOutput(string Name, int Width, string Description);

internal sealed class MarkerOnnxRunner(InferenceSession session, bool ownsSession, string provider) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private static readonly string[] Inputs = ["input_ids", "attention_mask", "marker_pos", "marker_mask", "qtype"];

    internal static bool HasMarkerInputs(InferenceSession session) =>
        session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Inputs);

    internal static void ValidateInputs(InferenceSession session, string provider)
    {
        if (!HasMarkerInputs(session)) throw new ArgumentException($"{provider} requires exactly five named inputs.");
        foreach (string name in Inputs)
            ValidateMetadata(name, session.InputMetadata[name], name == "marker_mask" ? typeof(bool) : typeof(long),
                name == "qtype" ? 1 : 2, provider);
    }

    internal static void ValidateMetadata(string name, NodeMetadata value, Type elementType, int rank, string provider)
    {
        if (!value.IsTensor || value.ElementType != elementType || value.Dimensions.Length != rank)
            throw new ArgumentException($"Invalid {provider} graph tensor {name}.");
    }

    internal Tensor<float>[] Run(MarkerInputs batch, MarkerOutput[] requestedOutputs, object owner,
        CancellationToken cancellationToken, Action<Tensor<float>[]>? validateOutputs = null)
    {
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, owner);
            long[] sequence = [batch.Rows, batch.Length], markers = [batch.Rows, batch.Width], types = [batch.Rows];
            foreach (var input in session.InputMetadata)
            {
                var shape = input.Key == "qtype" ? types : input.Key is "marker_pos" or "marker_mask" ? markers : sequence;
                if (input.Value.Dimensions.Where((d, i) => d >= 0 && d != shape[i]).Any())
                    throw new ArgumentException($"Batch does not fit static {provider} graph input {input.Key}.");
            }
            // Individual declarations release earlier bindings if a later allocation fails.
            using var ids = OrtValue.CreateTensorValueFromMemory(batch.Ids, sequence);
            using var attention = OrtValue.CreateTensorValueFromMemory(batch.Attention, sequence);
            using var positions = OrtValue.CreateTensorValueFromMemory(batch.Positions, markers);
            using var masks = OrtValue.CreateTensorValueFromMemory(batch.Masks, markers);
            using var qtypes = OrtValue.CreateTensorValueFromMemory(batch.Types, types);
            using var options = new RunOptions();
            using var registration = cancellationToken.Register(() => options.Terminate = true);
            try
            {
                using var outputs = session.Run(options, new Dictionary<string, OrtValue>
                {
                    ["input_ids"] = ids, ["attention_mask"] = attention, ["marker_pos"] = positions,
                    ["marker_mask"] = masks, ["qtype"] = qtypes
                }, requestedOutputs.Select(o => o.Name).ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                var result = new Tensor<float>[requestedOutputs.Length];
                for (int i = 0; i < result.Length; i++)
                {
                    var expected = requestedOutputs[i];
                    var info = outputs[i].GetTensorTypeAndShape();
                    if (info.ElementDataType != TensorElementType.Float ||
                        !info.Shape.SequenceEqual(new long[] { batch.Rows, expected.Width }))
                        throw new DecisionProtocolException($"Unexpected {expected.Description} shape or type.");
                    result[i] = Tensor.Create(outputs[i].GetTensorDataAsSpan<float>().ToArray(), [batch.Rows, expected.Width]);
                }
                // Provider-specific scoring validation remains coordinated with Score/Dispose.
                validateOutputs?.Invoke(result);
                return result;
            }
            catch (OnnxRuntimeException error) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"{provider} ONNX inference was cancelled.", error, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; if (ownsSession) session.Dispose(); } }
        finally { _gate.Release(); }
    }
}

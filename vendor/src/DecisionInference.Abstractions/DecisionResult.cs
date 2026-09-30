using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace DecisionInference;

public sealed class DecisionProtocolException(string message) : Exception(message);

/// <summary>Numerical representation of reported probabilities, not model calibration or accuracy.</summary>
public enum DecisionPrecision
{
    HighPrecision,
    /// <summary>Nearest four-decimal observations; interval endpoints conservatively include rounding ties.</summary>
    FourDecimalPlaces
}

/// <summary>An immutable portable decision payload with mutable, provider-specific extension metadata.</summary>
public abstract class DecisionAnswer
{
    public string QuestionId { get; }
    public DecisionKind Kind { get; }
    public DecisionPrecision Precision { get; }
    /// <summary>Gets or sets provider-specific raw data, excluded from ordinary JSON serialization.</summary>
    /// <remarks>Arbitrary assigned references are not cloned; providers document their snapshot ownership.</remarks>
    [JsonIgnore]
    public object? RawRepresentation { get; set; }
    /// <summary>Gets or sets mutable extension metadata; referenced values are not deep-copied.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }

    private protected DecisionAnswer(string questionId, DecisionKind kind, DecisionPrecision precision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (!Enum.IsDefined(precision)) throw new ArgumentOutOfRangeException(nameof(precision));
        QuestionId = questionId;
        Kind = kind;
        Precision = precision;
    }

    public const double DistributionTolerance = 1e-6;
    internal static void Unit(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new DecisionProtocolException("Probability must be finite and in [0,1].");
    }
    internal static void Distribution(IReadOnlyCollection<double> values, DecisionPrecision precision)
    {
        double sum = 0;
        foreach (double p in values) { Unit(p); sum += p; }
        if (precision == DecisionPrecision.FourDecimalPlaces)
        {
            decimal lower = 0, upper = 0;
            foreach (double p in values)
            {
                var interval = DecisionRounding.Interval(p, 1);
                lower += interval.Lower;
                upper += interval.Upper;
            }
            if (lower > 1 || upper < 1)
                throw new DecisionProtocolException("Rounded observations cannot represent a normalized distribution.");
        }
        else if (Math.Abs(sum - 1) > DistributionTolerance)
            throw new DecisionProtocolException("Distribution sum outside absolute tolerance 1e-6.");
    }
}

public sealed class BinaryDecisionAnswer : DecisionAnswer
{
    public double TrueProbability { get; }
    public BinaryDecisionAnswer(string questionId, double trueProbability, DecisionPrecision precision = DecisionPrecision.HighPrecision)
        : base(questionId, DecisionKind.Binary, precision)
    {
        Unit(trueProbability);
        if (precision == DecisionPrecision.FourDecimalPlaces) DecisionRounding.Interval(trueProbability, 1);
        TrueProbability = trueProbability;
    }
}

public sealed class ChoiceDecisionAnswer : DecisionAnswer
{
    public string SelectedCandidateId { get; }
    public IReadOnlyDictionary<string, double> Probabilities { get; }
    public ChoiceDecisionAnswer(string questionId, string selectedCandidateId,
        IEnumerable<KeyValuePair<string, double>> probabilities, DecisionPrecision precision = DecisionPrecision.HighPrecision)
        : base(questionId, DecisionKind.Choice, precision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedCandidateId);
        ArgumentNullException.ThrowIfNull(probabilities);
        Dictionary<string, double> copy = new(StringComparer.Ordinal);
        foreach (var p in probabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(p.Key);
            if (!copy.TryAdd(p.Key, p.Value)) throw new DecisionProtocolException("Duplicate candidate ID.");
        }
        Distribution(copy.Values, precision);
        if (!copy.ContainsKey(selectedCandidateId)) throw new DecisionProtocolException("Unknown selected candidate.");
        SelectedCandidateId = selectedCandidateId;
        Probabilities = new ReadOnlyDictionary<string, double>(copy);
    }
}

public sealed class ScoreDecisionAnswer : DecisionAnswer
{
    public IReadOnlyList<double> Probabilities { get; }
    /// <summary>Sum of index times observed probability. For rounded observations this is only an estimate and may exceed the rubric range.</summary>
    public double ExpectedIndex { get; }
    /// <summary>Lower feasible latent expectation for rounded observations; high precision uses the retained validation tolerance.</summary>
    public double ExpectedIndexLowerBound { get; }
    /// <summary>Upper feasible latent expectation, not a confidence interval or accuracy guarantee.</summary>
    public double ExpectedIndexUpperBound { get; }
    public double? NativeScore { get; }
    public ScoreDecisionAnswer(string questionId, IEnumerable<double> probabilities,
        double? nativeScore = null, DecisionPrecision precision = DecisionPrecision.HighPrecision)
        : base(questionId, DecisionKind.Score, precision)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        var copy = probabilities.ToArray();
        if (copy.Length < 2) throw new DecisionProtocolException("An ordinal distribution requires at least two levels.");
        Distribution(copy, precision);
        ExpectedIndex = copy.Select((p, i) => i * p).Sum();
        double tolerance = DistributionTolerance * (copy.Length - 1);
        ExpectedIndexLowerBound = Math.Max(0, ExpectedIndex - tolerance);
        ExpectedIndexUpperBound = Math.Min(copy.Length - 1, ExpectedIndex + tolerance);
        if (precision == DecisionPrecision.FourDecimalPlaces)
        {
            var bounds = DecisionRounding.ExpectedBounds(copy);
            ExpectedIndexLowerBound = (double)bounds.Lower;
            ExpectedIndexUpperBound = (double)bounds.Upper;
            if (nativeScore is { } rounded)
            {
                var interval = DecisionRounding.Interval(rounded, copy.Length - 1);
                if (interval.Upper < bounds.Lower || interval.Lower > bounds.Upper)
                    throw new DecisionProtocolException("Native score is incompatible with rounded probability observations.");
            }
        }
        else if (nativeScore is { } n && (!double.IsFinite(n) || n < 0 || n > copy.Length - 1 ||
            Math.Abs(n - ExpectedIndex) > tolerance))
            throw new DecisionProtocolException("Native score disagrees with the distribution's expected index.");
        NativeScore = nativeScore;
        Probabilities = Array.AsReadOnly(copy);
    }
}

internal static class DecisionRounding
{
    internal static (decimal Lower, decimal Upper) Interval(double value, int maximum)
    {
        if (!double.IsFinite(value) || value < 0 || value > maximum)
            throw new DecisionProtocolException("Rounded value must be finite and inside its domain.");
        decimal observed = decimal.Round((decimal)value, 4, MidpointRounding.ToEven);
        // A JSON four-decimal number has one canonical nearest-double representation.
        if (value != (double)observed)
            throw new DecisionProtocolException("Value does not lie on the declared four-decimal grid.");
        return (Math.Max(0m, observed - 0.00005m), Math.Min(maximum, observed + 0.00005m));
    }

    internal static (decimal Lower, decimal Upper) ExpectedBounds(double[] values)
    {
        var intervals = values.Select(p => Interval(p, 1)).ToArray();
        decimal baseMass = intervals.Sum(p => p.Lower);
        decimal baseIndex = intervals.Select((p, i) => i * p.Lower).Sum();
        decimal Extremum(bool descending)
        {
            decimal remaining = 1 - baseMass, result = baseIndex;
            for (int step = 0; step < intervals.Length && remaining > 0; step++)
            {
                int i = descending ? intervals.Length - 1 - step : step;
                decimal take = Math.Min(remaining, intervals[i].Upper - intervals[i].Lower);
                result += i * take;
                remaining -= take;
            }
            return result;
        }
        return (Extremum(false), Extremum(true));
    }
}

/// <summary>Validated, ordered answers and copied usage, with mutable raw data and extension properties.</summary>
public sealed class DecisionResult
{
    public string ModelId { get; }
    public IReadOnlyList<DecisionAnswer> Answers { get; }
    public UsageDetails? Usage { get; }
    /// <summary>Gets or sets provider-specific raw data, excluded from ordinary JSON serialization.</summary>
    /// <remarks>Arbitrary assigned references are not cloned; providers document their snapshot ownership.</remarks>
    [JsonIgnore]
    public object? RawRepresentation { get; set; }
    /// <summary>Gets or sets mutable extension metadata; referenced values are not deep-copied.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }

    public DecisionResult(DecisionInput input, string modelId, IEnumerable<DecisionAnswer> answers,
        UsageDetails? usage = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(answers);
        var copy = answers.ToArray();
        if (copy.Length != input.Questions.Count || copy.Any(a => a is null) ||
            copy.Select(a => a.QuestionId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new DecisionProtocolException("Missing, extra, duplicate or null answers.");
        var map = copy.ToDictionary(a => a.QuestionId, StringComparer.Ordinal);
        foreach (var q in input.Questions)
        {
            if (!map.TryGetValue(q.Id, out var answer) || answer.Kind != q.Kind)
                throw new DecisionProtocolException("Answer ID or kind mismatch.");
            if (q is ChoiceDecisionQuestion c && answer is ChoiceDecisionAnswer a &&
                !a.Probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(c.Candidates.Select(c => c.Id)))
                throw new DecisionProtocolException("Incomplete or unexpected candidate distribution.");
            if (q is ScoreDecisionQuestion s && answer is ScoreDecisionAnswer b && b.Probabilities.Count != s.Rubric.Count)
                throw new DecisionProtocolException("Incomplete or unexpected rubric distribution.");
        }
        if (usage is { } u && (u.InputTokenCount < 0 || u.OutputTokenCount < 0 || u.TotalTokenCount < 0 ||
            (u.InputTokenCount is { } inputTokens && u.OutputTokenCount is { } output &&
                (inputTokens > long.MaxValue - output || (u.TotalTokenCount is { } total && total != inputTokens + output)))))
            throw new DecisionProtocolException("Invalid observed usage.");
        ModelId = modelId;
        Answers = Array.AsReadOnly(input.Questions.Select(q => map[q.Id]).ToArray());
        if (usage is not null)
        {
            Usage = new();
            Usage.Add(usage);
        }
    }
}

using System.Text.Json;

namespace DecisionInference;

public sealed class DecisionInput
{
    public JsonElement State { get; }
    public IReadOnlyList<DecisionQuestion> Questions { get; }

    public DecisionInput(JsonElement state, IEnumerable<DecisionQuestion> questions)
    {
        if (state.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("State must be explicit JSON.", nameof(state));
        ArgumentNullException.ThrowIfNull(questions);
        var copy = questions.ToArray();
        if (copy.Length == 0 || copy.Any(q => q is null) ||
            copy.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Questions must be nonempty with unique, nonnull IDs.", nameof(questions));
        State = state.Clone();
        Questions = Array.AsReadOnly(copy);
    }
}

public abstract class DecisionQuestion
{
    public string Id { get; }
    public DecisionKind Kind { get; }
    /// <summary>Gets explicit semantic instructions; opaque IDs never supply missing intent.</summary>
    public string Instructions { get; }

    private protected DecisionQuestion(string id, DecisionKind kind, string? instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Kind = kind;
        Instructions = instructions ?? kind switch
        {
            DecisionKind.Choice => "Choose one option.",
            DecisionKind.Score => "Assess the state against the ordered rubric.",
            _ => ""
        };
    }
}

public sealed class BinaryDecisionQuestion : DecisionQuestion
{
    public string? TrueDescription { get; }
    public string? FalseDescription { get; }

    public BinaryDecisionQuestion(string id, string? instructions = null,
        string? trueDescription = null, string? falseDescription = null)
        : base(id, DecisionKind.Binary, instructions)
    {
        if (trueDescription is not null) ArgumentException.ThrowIfNullOrWhiteSpace(trueDescription);
        if (falseDescription is not null) ArgumentException.ThrowIfNullOrWhiteSpace(falseDescription);
        if (string.IsNullOrWhiteSpace(instructions) &&
            (string.IsNullOrWhiteSpace(trueDescription) || string.IsNullOrWhiteSpace(falseDescription)))
            throw new ArgumentException("Binary decisions require meaningful instructions or both true and false descriptions.", nameof(instructions));
        TrueDescription = trueDescription;
        FalseDescription = falseDescription;
    }
}

public sealed class DecisionCandidate
{
    public string Id { get; }
    public string Description { get; }

    public DecisionCandidate(string id, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Id = id;
        Description = description;
    }
}

public sealed class ChoiceDecisionQuestion : DecisionQuestion
{
    public IReadOnlyList<DecisionCandidate> Candidates { get; }

    public ChoiceDecisionQuestion(string id, string? instructions, IEnumerable<DecisionCandidate> candidates)
        : base(id, DecisionKind.Choice, instructions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var copy = candidates.ToArray();
        if (copy.Length == 0 || copy.Any(c => c is null) ||
            copy.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Candidates must be nonempty with unique, nonnull IDs.");
        Candidates = Array.AsReadOnly(copy);
    }
}

public sealed class ScoreDecisionQuestion : DecisionQuestion
{
    public IReadOnlyList<string> Rubric { get; }

    public ScoreDecisionQuestion(string id, string? instructions, IEnumerable<string> rubric)
        : base(id, DecisionKind.Score, instructions)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        var copy = rubric.Select(r => !string.IsNullOrWhiteSpace(r) ? r :
            throw new ArgumentException("Rubric descriptions must be meaningful.", nameof(rubric))).ToArray();
        if (copy.Length < 2) throw new ArgumentException("An ordinal rubric requires at least two levels.");
        Rubric = Array.AsReadOnly(copy);
    }
}

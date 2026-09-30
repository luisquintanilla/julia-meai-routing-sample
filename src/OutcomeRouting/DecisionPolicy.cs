using System.Text.Json;
using System.Text.Json.Serialization;
using DecisionInference;

namespace OutcomeRouting;

public sealed record PolicySettings
{
    public double ConfidenceFloor { get; init; } = 0.65;
    public Tier ConservativeTier { get; init; } = Tier.Balanced;
    public int EvidenceLimit { get; init; } = 4;
    public TimeSpan EvidenceAge { get; init; } = TimeSpan.FromDays(7);
    public int MaximumAttempts { get; init; } = 3;

    public void Validate()
    {
        if (!double.IsFinite(ConfidenceFloor) || ConfidenceFloor is < 0 or > 1 ||
            !Enum.IsDefined(ConservativeTier) || EvidenceLimit is < 1 or > 6 ||
            EvidenceAge <= TimeSpan.Zero || EvidenceAge > TimeSpan.FromDays(30) ||
            MaximumAttempts is < 1 or > 6)
            throw new ArgumentException("Invalid confidence, conservative tier, history window or attempt limit.");
    }
}

public sealed record ProjectedEvidence(string Route, string Tier, string Outcome, string Provenance);
public sealed record ProjectedRoute(string Route, string Tier, string Model);
public sealed record DecisionState(string Task, string Context, string Cohort, string Required, string Minimum,
    ProjectedRoute[] Routes, ProjectedEvidence[] Evidence);

[JsonSerializable(typeof(DecisionState))]
internal partial class RoutingJson : JsonSerializerContext;

public sealed class DecisionPolicy(IDecisionGenerator generator, RouteCatalog catalog, PolicySettings settings)
{
    public const string QuestionId = "minimum-tier";
    public static string Id(Tier tier) => tier.ToString().ToLowerInvariant();
    public static IReadOnlyList<DecisionQuestion> Questions { get; } = Array.AsReadOnly<DecisionQuestion>(
    [
        new ChoiceDecisionQuestion(QuestionId,
            "Choose the minimum sufficient chat capability for this task, considering relevant known outcomes.",
            [new("fast", "Routine, clear, short transformations"),
             new("balanced", "Multi-step work or routine work with recent failures"),
             new("strong", "Complex reasoning, high risk, or failures at balanced capability")])
    ]);

    public async Task<DecisionSnapshot> DecideAsync(RoutingTask task, IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken)
    {
        settings.Validate();
        if (evidence.Count > settings.EvidenceLimit)
            throw new ArgumentException("Evidence must already be bounded.", nameof(evidence));
        if (evidence.Any(e => e.Outcome == Outcome.Unknown || e.Provenance == Provenance.None ||
            !Enum.IsDefined(e.Outcome) || !Enum.IsDefined(e.Provenance) ||
            !catalog.Routes.Any(r => r.Identity == e.RouteIdentity && r.Tier == e.Tier && r.Name == e.Route)))
            throw new ArgumentException("Evidence must be known and match the current concrete route configuration.", nameof(evidence));
        var state = new DecisionState(task.Task, task.Context, task.Cohort, task.Required.ToString(), Id(task.MinimumTier),
            catalog.Routes.Select(r => new ProjectedRoute(r.Name, Id(r.Tier), r.Model)).ToArray(),
            evidence.Select(e => new ProjectedEvidence(e.Route, Id(e.Tier), e.Outcome.ToString(), e.Provenance.ToString())).ToArray());
        string projection = JsonSerializer.Serialize(state, RoutingJson.Default.DecisionState);
        var result = await generator.GenerateAsync(state, Questions, RoutingJson.Default.DecisionState,
            cancellationToken: cancellationToken);
        var answer = result.GetAnswer<ChoiceDecisionAnswer>(QuestionId);
        if (!answer.Probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["fast", "balanced", "strong"]) ||
            !Enum.TryParse<Tier>(answer.SelectedCandidateId, true, out var recommended) ||
            answer.Probabilities[answer.SelectedCandidateId] + DecisionAnswer.DistributionTolerance < answer.Probabilities.Values.Max())
            throw new DecisionProtocolException("Expected the winning exact fast/balanced/strong candidate and full distribution.");

        List<string> reasons = [];
        Tier selected = recommended;
        if (answer.Probabilities[answer.SelectedCandidateId] < settings.ConfidenceFloor)
        {
            selected = settings.ConservativeTier;
            reasons.Add($"low-confidence:{Id(settings.ConservativeTier)}");
        }
        if (selected < task.MinimumTier)
        {
            selected = task.MinimumTier;
            reasons.Add($"application-minimum:{Id(selected)}");
        }
        foreach (var failure in evidence.Where(e => e.Outcome == Outcome.Failure))
        {
            Tier floor = failure.Tier == Tier.Strong ? Tier.Strong : failure.Tier + 1;
            if (selected < floor) selected = floor;
            string source = failure.Provenance == Provenance.Verifier ? "verified" : "application";
            reasons.Add($"recent-{source}-failure:{failure.Route}:minimum-{Id(floor)}");
        }
        var eligible = catalog.Routes.Where(r => r.Tier >= selected && (r.Capabilities & task.Required) == task.Required)
            .OrderBy(r => r.Tier).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("No route meets the policy tier and declared capabilities.");
        if (eligible[0].Tier != selected)
        {
            selected = eligible[0].Tier;
            reasons.Add($"capability-floor:{Id(selected)}");
        }
        if (reasons.Count == 0) reasons.Add("decision-signal");
        return new(result.ModelId, recommended, answer.Probabilities, selected, reasons.AsReadOnly(), projection, result.Usage?.InputTokenCount);
    }
}

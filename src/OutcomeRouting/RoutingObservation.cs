using System.Collections.ObjectModel;

namespace OutcomeRouting;

public enum RoutingObservationPhase
{
    RouteSelected,
    AttemptFinished
}

/// <summary>Request-correlated routing telemetry, without prompts, output or projected decision state.</summary>
public sealed record RoutingObservation(
    Guid RunId,
    RoutingObservationPhase Phase,
    string DecisionModel,
    Tier RecommendedTier,
    Tier SelectedTier,
    IReadOnlyDictionary<string, double> Probabilities,
    IReadOnlyList<string> Reasons,
    string Route,
    string RouteIdentity,
    Tier RouteTier,
    AttemptRecord? Attempt)
{
    internal static RoutingObservation Create(
        RequestSession session,
        ChatRoute route,
        AttemptRecord? attempt = null)
    {
        var decision = session.Decision ?? throw new InvalidOperationException("Observation requires a decision.");

        return new RoutingObservation(
            session.Task.RunId,
            attempt is null ? RoutingObservationPhase.RouteSelected : RoutingObservationPhase.AttemptFinished,
            decision.Model,
            decision.Recommended,
            decision.Selected,
            new ReadOnlyDictionary<string, double>(decision.Probabilities.ToDictionary()),
            Array.AsReadOnly(decision.Reasons.ToArray()),
            route.Name,
            route.Identity,
            route.Tier,
            attempt);
    }
}

/// <summary>
/// An optional awaited observer. Calls are ordered within a request but may overlap across requests.
/// Selection uses the request token; persisted attempt notifications use CancellationToken.None for cleanup.
/// Exceptions propagate and terminate the request, without observer-driven failover or fabricated quality.
/// </summary>
public interface IRoutingObserver
{
    ValueTask ObserveAsync(RoutingObservation observation, CancellationToken cancellationToken);
}

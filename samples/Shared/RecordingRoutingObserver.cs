using System.Collections.Concurrent;
using OutcomeRouting;

namespace RoutingSamples;

/// <summary>In-memory sample collector. No raw text; callers control retention and persistence.</summary>
internal sealed class RecordingRoutingObserver : IRoutingObserver
{
    private readonly ConcurrentQueue<RoutingObservation> _observations = new();

    internal IReadOnlyList<RoutingObservation> Snapshot() => Array.AsReadOnly(_observations.ToArray());

    public ValueTask ObserveAsync(RoutingObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _observations.Enqueue(observation);
        return ValueTask.CompletedTask;
    }
}

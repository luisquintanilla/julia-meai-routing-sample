using System.Globalization;
using OutcomeRouting;

namespace RoutingSamples;

/// <summary>Sample output, not library logging. Each correlated line is written atomically.</summary>
internal sealed class ConsoleRoutingObserver(TextWriter output) : IRoutingObserver
{
    private readonly object _gate = new();

    public ValueTask ObserveAsync(RoutingObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string line;
        if (observation.Phase == RoutingObservationPhase.RouteSelected)
        {
            string probabilities = string.Join(", ", Enum.GetValues<Tier>().Select(tier =>
            {
                string candidate = DecisionPolicy.Id(tier);
                string probability = observation.Probabilities[candidate].ToString("G6", CultureInfo.InvariantCulture);
                return $"{candidate}:{probability}";
            }));

            line = $"decision: recommended={DecisionPolicy.Id(observation.RecommendedTier)} " +
                $"probabilities=[{probabilities}] policy={DecisionPolicy.Id(observation.SelectedTier)} " +
                $"route={observation.Route}";
        }
        else
        {
            var attempt = observation.Attempt ?? throw new InvalidOperationException("Attempt observation lacks telemetry.");
            line = $"attempt {attempt.Number}: actual={observation.Route} " +
                $"completed={attempt.ResponseCompleted} committed={attempt.OutputCommitted}";
        }

        lock (_gate)
        {
            output.WriteLine($"[{observation.RunId:N}] {line}");
        }

        return ValueTask.CompletedTask;
    }
}

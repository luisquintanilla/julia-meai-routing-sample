using System.Globalization;
using DecisionInference;
using Microsoft.Extensions.AI;
using OutcomeRouting;

internal static class Demonstrations
{
    internal static RoutingTask Task(string cohort)
    {
        return new RoutingTask(
            cohort,
            "Sort the integers [3,1,2] ascending. Return only the JSON array, with no prose.",
            required: Capability.Text | Capability.Json);
    }

    internal static async System.Threading.Tasks.Task RunAsync()
    {
        Console.WriteLine("OFFLINE DEMO: BOTH decision signals and downstream responses are SIMULATED.");
        Console.WriteLine("Real MEAI FailoverChatClient + real SQLite + independent exact-sort verifier.");

        string path = Path.Combine(".routing", $"demo-{Guid.NewGuid():N}", "history.db");
        var settings = new PolicySettings();
        using IDecisionGenerator generator = new FixtureDecisionGenerator();
        using var clients = ClientSet.Fixtures(FixtureBehavior.Incorrect);
        var catalog = clients.Catalog;
        var store = new SqliteOutcomeStore(path);

        using var router = new OutcomeRouter(generator, catalog, settings, store);
        var application = new OutcomeApplication(router, catalog, store);
        var verifier = new ExactSortVerifier([3, 1, 2]);

        var first = await application.ExecuteAsync(Task("sort-verified-v1"), verifier);
        Print("1 initial", first);

        Require(
            first.Decision.Recommended == Tier.Fast &&
            first.Decision.Selected == Tier.Fast &&
            first.ActualRoute == "fast" &&
            first.Feedback.Outcome == Outcome.Failure &&
            first.Attempts.Count == 1,
            "Initial fast response must be a semantic failure, not a transport retry.");

        var reopened = new SqliteOutcomeStore(path);
        using var nextRouter = new OutcomeRouter(generator, catalog, settings, reopened);
        var nextApplication = new OutcomeApplication(nextRouter, catalog, reopened);
        var next = await nextApplication.ExecuteAsync(Task("sort-verified-v1"), verifier);
        Print("2 after persisted failure/reload", next);

        Require(
            next.Decision.Recommended == Tier.Fast &&
            next.Decision.Selected == Tier.Balanced &&
            next.ActualRoute == "balanced" &&
            next.Feedback.Outcome == Outcome.Success &&
            next.Decision.ProjectedState.Contains("\"Outcome\":\"Failure\"", StringComparison.Ordinal),
            "Verified failure must enter bounded state and raise the next request's minimum tier.");

        var unknown = await application.ExecuteAsync(Task("sort-unknown-v1"), new ExplicitUnknownVerifier());
        Print("3 explicit unknown", unknown);

        var afterUnknown = await nextApplication.ExecuteAsync(Task("sort-unknown-v1"), null);
        Print("4 after unknown", afterUnknown);

        Require(
            unknown.Feedback.Outcome == Outcome.Unknown &&
            afterUnknown.Decision.Selected == Tier.Fast &&
            reopened.ReadEvidence("sort-unknown-v1", catalog.Revision, settings).Count == 0,
            "Unknown and transport-only completion must not supply learning evidence.");

        using var failoverClients = ClientSet.Fixtures(FixtureBehavior.FailBeforeOutput);
        using var failoverRouter = new OutcomeRouter(generator, failoverClients.Catalog, settings, store);
        var failoverApplication = new OutcomeApplication(failoverRouter, failoverClients.Catalog, store);
        var alternate = await failoverApplication.ExecuteAsync(Task("sort-health-v1"), verifier);
        Print("5 pre-output failover", alternate);

        var evidence = reopened.ReadEvidence("sort-health-v1", failoverClients.Catalog.Revision, settings);

        Require(
            alternate.Attempts.Count == 2 &&
            alternate.Attempts[0].ErrorType is not null &&
            !alternate.Attempts[0].ResponseCompleted &&
            alternate.Attempts[1].ResponseCompleted &&
            alternate.ActualRoute == "balanced" &&
            alternate.Feedback.Outcome == Outcome.Success &&
            evidence.Count == 1 &&
            evidence[0].Route == "balanced",
            "Verification must be attributed to the actual completed alternate, not failed fast.");

        Require(
            router.PendingRequestCount == 0 &&
            nextRouter.PendingRequestCount == 0 &&
            failoverRouter.PendingRequestCount == 0,
            "Request routing state must be cleaned up.");

        Console.WriteLine("All offline transition, persistence, unknown and actual-route attribution assertions passed.");
        Console.WriteLine($"Isolated demo database: {Path.GetFullPath(path)}");
        Console.WriteLine("Fixture probabilities are not Julia accuracy/performance results. No weights or cloud calls were used.");
    }

    internal static void Print(string label, ExecutionResult result)
    {
        var probabilities = result.Decision.Probabilities.Select(
            probability => $"{probability.Key}:{probability.Value.ToString("G6", CultureInfo.InvariantCulture)}");

        var attempts = result.Attempts.Select(
            attempt => $"{attempt.Route}:{(attempt.ErrorType is null ? "completed" : "provider-failed")}");

        Console.WriteLine(
            $"{label}: signal={DecisionPolicy.Id(result.Decision.Recommended)} " +
            $"distribution=[{string.Join(", ", probabilities)}] " +
            $"policy={DecisionPolicy.Id(result.Decision.Selected)} actual={result.ActualRoute} " +
            $"outcome={result.Feedback.Outcome} provenance={result.Feedback.Provenance}");

        Console.WriteLine(
            $"  reasons={string.Join(", ", result.Decision.Reasons)}; " +
            $"attempts={string.Join(" -> ", attempts)}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Demo assertion failed: {message}");
        }
    }

    private sealed class ExplicitUnknownVerifier : ITaskVerifier
    {
        public Feedback Verify(ChatResponse response)
        {
            return Feedback.Unknown("verification-not-available");
        }
    }
}

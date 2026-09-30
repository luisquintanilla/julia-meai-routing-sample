using System.Text.Json;
using DecisionInference;
using DecisionInference.Julia;
using OutcomeRouting;
using RoutingSamples;

internal static class NativeScenario
{
    internal static async Task RunAsync(Arguments arguments)
    {
        // Reject incomplete live configuration before loading weights.
        var live = arguments.Live ? LiveConfiguration.FromEnvironment() : null;
        string directory = arguments.JuliaDirectory!;
        NativeAssets.Verify(directory);

        using IDecisionGenerator generator = JuliaDecisionGenerator.LoadFromDirectory(
            directory,
            options => options.IntraOpNumThreads = 2);

        using var clients = live?.CreateClients() ?? ClientSet.Fixtures(FixtureBehavior.Correct);
        var catalog = clients.Catalog;
        string defaultStore = live is null ? @".routing\julia-fixture.db" : @".routing\julia-live.db";
        var store = new SqliteOutcomeStore(arguments.Store ?? defaultStore);

        using var router = new OutcomeRouter(generator, catalog, arguments.Policy, store);
        var application = new OutcomeApplication(router, catalog, store);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Console.WriteLine(live is null
            ? "REAL Julia-1 CPU decision inference; SIMULATED downstream chat responses."
            : "REAL Julia-1 CPU decision inference; LIVE OpenAI-compatible downstream chat (explicit opt-in).");

        var result = await application.ExecuteAsync(
            Demonstrations.Task("sort-three-v1"),
            arguments.NoVerifier ? null : new ExactSortVerifier([3, 1, 2]),
            cancellationToken: deadline.Token);

        Demonstrations.Print("native", result);
        Console.WriteLine($"Run ID: {result.RunId}; actual route identity: {store.GetRun(result.RunId).ActualRouteIdentity}");
        Console.WriteLine($"Database: {store.Path}");
        Console.WriteLine($"Decision state (not stored): {result.Decision.ProjectedState}");

        PrintTokenBudget(directory, result.Decision.ProjectedState, deadline.Token);
        Console.WriteLine("No weight updates. This invocation is not a route-quality, calibration or performance benchmark.");
    }

    private static void PrintTokenBudget(
        string directory,
        string projectedState,
        CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(projectedState);
        using var preparation = new PrepareDecisionInputs(Path.Combine(directory, "tokenizer.json"));
        var packed = preparation.Prepare(
            new DecisionInput(json.RootElement, DecisionPolicy.Questions),
            cancellationToken);

        long combinedTokens = 0;

        for (int row = 0; row < packed.BatchSize; row++)
        {
            for (int column = 0; column < packed.SequenceLength; column++)
            {
                combinedTokens += packed.AttentionMask[row, column];
            }
        }

        Console.WriteLine(
            $"Julia budget: {combinedTokens} combined tokens; " +
            $"{packed.SequenceLength} padded tokens; limits 1024 total / 256 head / 48 per option.");
    }
}

using DecisionInference.Julia;
using Microsoft.Extensions.AI;
using OutcomeRouting;
using RoutingSamples;

if (args is ["--help"])
{
    Console.WriteLine(@"dotnet run --project samples\JuliaRouting -- <local-julia-directory>");
    return 0;
}

try
{
    if (args.Length != 1)
    {
        throw new ArgumentException("Supply one local Julia asset directory, or --help. Nothing is downloaded.");
    }

    string assetDirectory = Path.GetFullPath(args[0]);
    NativeAssets.Verify(assetDirectory);

    Console.WriteLine("REAL Julia decisions. SIMULATED chat responses.");

    using var decisions = JuliaDecisionGenerator.LoadFromDirectory(assetDirectory);
    using var fast = new DemoChatClient("demo-fast", "[1,2,3]");
    using var balanced = new DemoChatClient("demo-balanced", "[1,2,3]");
    using var strong = new DemoChatClient("demo-strong", "[1,2,3]");

    using IChatClient chatClient = new OutcomeRoutingChatClient(
        decisions,
        [
            ChatRoute.Create(Tier.Fast, fast),
            ChatRoute.Create(Tier.Balanced, balanced),
            ChatRoute.Create(Tier.Strong, strong)
        ],
        new OutcomeRoutingOptions
        {
            HistoryPath = @".routing\julia-quickstart.db",
            Cohort = "sort-integers-v1",
            VerifierFactory = SortVerification.ForMessages
        })
        .AsBuilder()
        .Build();

    ChatResponse response = await chatClient.GetResponseAsync(
        "Sort [3,1,2] ascending. Return only the JSON array.");
    Console.WriteLine(response.Text);

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException or DirectoryNotFoundException or FileNotFoundException or InvalidDataException
        ? $"ERROR ({error.GetType().Name}): {error.Message}"
        : $"ERROR ({error.GetType().Name}): native execution failed; no fixture fallback was used.");

    return 2;
}

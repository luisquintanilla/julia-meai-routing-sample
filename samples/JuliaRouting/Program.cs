using DecisionInference;
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

    using IDecisionGenerator decisions = JuliaDecisionGenerator.LoadFromDirectory(assetDirectory);
    using IChatClient fast = new DemoChatClient("demo-fast", "[1,2,3]");
    using IChatClient balanced = new DemoChatClient("demo-balanced", "[1,2,3]");
    using IChatClient strong = new DemoChatClient("demo-strong", "[1,2,3]");

    ChatRoute[] routes =
    [
        ChatRoute.Create(Tier.Fast, fast),
        ChatRoute.Create(Tier.Balanced, balanced),
        ChatRoute.Create(Tier.Strong, strong)
    ];

    IOutcomeStore history = new SqliteOutcomeStore(@".routing\julia-quickstart.db");
    IRoutingObserver observer = new ConsoleRoutingObserver(Console.Out);

    var routingOptions = new OutcomeRoutingOptions
    {
        History = history,
        Cohort = "sort-integers-v1",
        Policy = new PolicySettings
        {
            ConfidenceFloor = 0.65,
            ConservativeTier = Tier.Balanced
        },
        VerifierFactory = SortVerification.ForMessages,
        Observer = observer
    };

    using IChatClient chatClient = new OutcomeRoutingChatClient(decisions, routes, routingOptions)
        .AsBuilder()
        .ConfigureOptions(options => options.TopP = 0.9f)
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

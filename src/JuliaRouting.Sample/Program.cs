using System.ClientModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DecisionInference;
using DecisionInference.Julia;
using Microsoft.Extensions.AI;
using OpenAI;
using OutcomeRouting;

try
{
    var arguments = Arguments.Parse(args);
    if (arguments.Help) { Console.WriteLine(Arguments.Usage); return 0; }
    arguments.Policy.Validate();
    if (arguments.JuliaDirectory is null)
    {
        if (arguments.Live || arguments.Store is not null || arguments.NoVerifier)
            throw new ArgumentException("--live, --store and --no-verifier require --julia <directory>; offline demo data is isolated.");
        if (arguments.Policy != new PolicySettings())
            throw new ArgumentException("Policy flags apply to --julia mode; the offline demonstration uses fixed asserted settings.");
        await Demonstrations.RunAsync();
        return 0;
    }

    // Validate live configuration before loading weights, and never create clients in default/offline mode.
    var live = arguments.Live ? LiveConfiguration.FromEnvironment() : null;
    NativeAssets.Verify(arguments.JuliaDirectory);
    using IDecisionGenerator generator = JuliaDecisionGenerator.LoadFromDirectory(arguments.JuliaDirectory,
        options => options.IntraOpNumThreads = 2);
    using var clients = live?.CreateClients() ?? ClientSet.Fixtures(FixtureBehavior.Correct);
    var catalog = clients.Catalog;
    var store = new SqliteOutcomeStore(arguments.Store ??
        (live is null ? @".routing\julia-fixture.db" : @".routing\julia-live.db"));
    using var router = new OutcomeRouter(generator, catalog, arguments.Policy, store);
    var app = new OutcomeApplication(router, catalog, store);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.WriteLine(live is null
        ? "REAL Julia-1 CPU decision inference; SIMULATED downstream chat responses."
        : "REAL Julia-1 CPU decision inference; LIVE OpenAI-compatible downstream chat (explicit opt-in).");
    var result = await app.ExecuteAsync(Demonstrations.Task("sort-three-v1"),
        arguments.NoVerifier ? null : new ExactSortVerifier([3, 1, 2]), cancellationToken: deadline.Token);
    Demonstrations.Print("native", result);
    Console.WriteLine($"Run ID: {result.RunId}; actual route identity: {store.GetRun(result.RunId).ActualRouteIdentity}");
    Console.WriteLine($"Database: {store.Path}");
    Console.WriteLine($"Decision state (not stored): {result.Decision.ProjectedState}");
    using var json = JsonDocument.Parse(result.Decision.ProjectedState);
    using var preparation = new PrepareDecisionInputs(Path.Combine(arguments.JuliaDirectory, "tokenizer.json"));
    var packed = preparation.Prepare(new DecisionInput(json.RootElement, DecisionPolicy.Questions), deadline.Token);
    long combinedTokens = 0;
    for (int row = 0; row < packed.BatchSize; row++)
        for (int column = 0; column < packed.SequenceLength; column++)
            combinedTokens += packed.AttentionMask[row, column];
    Console.WriteLine($"Julia budget: {combinedTokens} combined tokens; " +
        $"{packed.SequenceLength} padded tokens; limits 1024 total / 256 head / 48 per option.");
    Console.WriteLine("No weight updates. This invocation is not a route-quality, calibration or performance benchmark.");
    return 0;
}
catch (Exception error)
{
    // Do not print live provider exception messages: they may contain request bodies or endpoint secrets.
    Console.Error.WriteLine(error is ArgumentException or FormatException or DirectoryNotFoundException or FileNotFoundException or InvalidDataException
        ? $"ERROR ({error.GetType().Name}): {error.Message}"
        : $"ERROR ({error.GetType().Name}): execution failed; no fixture or empty-history fallback was used.");
    return 2;
}

internal sealed record Arguments(string? JuliaDirectory, string? Store, bool Live, bool NoVerifier, bool Help, PolicySettings Policy)
{
    internal const string Usage = """
        Julia outcome-aware routing (.NET 10)
          dotnet run --project src\JuliaRouting.Sample                       # isolated fixture demo
          dotnet run --project src\JuliaRouting.Sample -- --julia <directory> # real CPU Julia, fixture chat
          dotnet run --project src\JuliaRouting.Sample -- --julia <directory> --live
        Native/live options:
          --store <database-path>  --no-verifier
          --confidence-floor <0..1>  --conservative-tier <balanced|strong|fast>
          --history-limit <1..6>  --history-days <1..30>  --max-attempts <1..6>
        Live mode requires ROUTING_CHAT_ENDPOINT, ROUTING_CHAT_API_KEY, ROUTING_FAST_MODEL,
        ROUTING_BALANCED_MODEL and ROUTING_STRONG_MODEL in the environment (at least two distinct models).
        Optional ROUTING_<FAST|BALANCED|STRONG>_REASONING=<low|medium|high>.
        No automatic model downloads; --live is the only path making downstream network calls.
        """;

    internal static Arguments Parse(string[] args)
    {
        string? directory = null, store = null;
        bool live = false, noVerifier = false;
        var policy = new PolicySettings();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            if (!seen.Add(flag)) throw new ArgumentException($"Duplicate option {flag}.");
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"An explicit value is required for {flag}.");
                return args[i];
            }
            switch (flag)
            {
                case "--help" when args.Length == 1: return new(null, null, false, false, true, policy);
                case "--julia": directory = Path.GetFullPath(Value()); break;
                case "--store": store = Path.GetFullPath(Value()); break;
                case "--live": live = true; break;
                case "--no-verifier": noVerifier = true; break;
                case "--confidence-floor":
                    policy = policy with { ConfidenceFloor = double.Parse(Value(), CultureInfo.InvariantCulture) }; break;
                case "--conservative-tier":
                    string tier = Value();
                    if (tier is not ("fast" or "balanced" or "strong")) throw new ArgumentException("Use fast, balanced or strong.");
                    policy = policy with { ConservativeTier = Enum.Parse<Tier>(tier, true) }; break;
                case "--history-limit":
                    policy = policy with { EvidenceLimit = int.Parse(Value(), CultureInfo.InvariantCulture) }; break;
                case "--history-days":
                    policy = policy with { EvidenceAge = TimeSpan.FromDays(int.Parse(Value(), CultureInfo.InvariantCulture)) }; break;
                case "--max-attempts":
                    policy = policy with { MaximumAttempts = int.Parse(Value(), CultureInfo.InvariantCulture) }; break;
                default: throw new ArgumentException($"Unknown option {flag}. Use --help.");
            }
        }
        return new(directory, store, live, noVerifier, false, policy);
    }
}

internal static class NativeAssets
{
    internal static void Verify(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Julia asset directory not found: {directory}");
        (string File, string Hash)[] assets =
        [
            ("model.onnx", "97141d0cfb1da6204e9f8f24d581af72eaeb82cda21149d83eaa6df7160fbcd9"),
            ("model.onnx.data", "fd915be810d7ebfb80fb05a48dd33c9484d17ae1b6bcb9e1f544cbaaa913ded1"),
            ("tokenizer.json", "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f")
        ];
        foreach (var (file, expected) in assets)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing pinned Julia asset: {file}", path);
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (actual != expected) throw new InvalidDataException($"Julia asset hash mismatch: {file}; refusing unpinned inference.");
        }
        Console.WriteLine("All three Julia asset SHA256 hashes match the pinned export.");
    }
}

internal sealed class ClientSet(RouteCatalog catalog) : IDisposable
{
    internal RouteCatalog Catalog { get; } = catalog;
    internal static ClientSet Fixtures(FixtureBehavior fast) => new(new RouteCatalog(
        Enum.GetValues<Tier>().Select(t => new ChatRoute(DecisionPolicy.Id(t), t, Capability.Text | Capability.Json,
            "fixture://scripted", $"fixture-{DecisionPolicy.Id(t)}-v1",
            t == Tier.Fast ? $"scripted-{fast}" : "scripted-Correct",
            new ScriptedChatClient(t == Tier.Fast ? fast : FixtureBehavior.Correct)))));
    public void Dispose()
    {
        foreach (var route in Catalog.Routes) route.Client.Dispose();
    }
}

internal sealed record LiveConfiguration(Uri Endpoint, string ApiKey, string[] Models, string?[] Reasoning)
{
    internal static LiveConfiguration FromEnvironment()
    {
        static string Require(string name) => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing live configuration: {name}.");
        var endpoint = new Uri(Require("ROUTING_CHAT_ENDPOINT"), UriKind.Absolute);
        if ((endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTPS endpoint (HTTP only for loopback), without credentials/query/fragment.");
        string[] tiers = ["FAST", "BALANCED", "STRONG"];
        var models = tiers.Select(t => Require($"ROUTING_{t}_MODEL")).ToArray();
        if (models.Any(m => m.Length > 64) || models.Distinct(StringComparer.Ordinal).Count() < 2)
            throw new ArgumentException("Supply 2+ distinct model IDs, each at most 64 characters, mapped to all three tiers.");
        var reasoning = tiers.Select(t => Environment.GetEnvironmentVariable($"ROUTING_{t}_REASONING")).ToArray();
        if (reasoning.Any(r => r is not (null or "low" or "medium" or "high")))
            throw new ArgumentException("Configured reasoning effort must be low, medium or high, or absent.");
        return new(endpoint, Require("ROUTING_CHAT_API_KEY"), models, reasoning);
    }

    internal ClientSet CreateClients()
    {
        // Disable SDK retries so the MEAI attempt ledger reflects each configured route invocation.
        var openai = new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions
        {
            Endpoint = Endpoint,
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)
        });
        return new(new RouteCatalog(Enum.GetValues<Tier>().Select((tier, index) =>
            new ChatRoute(DecisionPolicy.Id(tier), tier, Capability.Text | Capability.Json,
                Endpoint.AbsoluteUri, Models[index], "openai-text-json-v1",
                openai.GetChatClient(Models[index]).AsIChatClient(), new(Reasoning: Reasoning[index])))));
    }
}

internal static class Demonstrations
{
    internal static RoutingTask Task(string cohort) => new(cohort,
        "Sort the integers [3,1,2] ascending. Return only the JSON array, with no prose.",
        required: Capability.Text | Capability.Json);

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
        var app = new OutcomeApplication(router, catalog, store);
        var verifier = new ExactSortVerifier([3, 1, 2]);
        var first = await app.ExecuteAsync(Task("sort-verified-v1"), verifier);
        Print("1 initial", first);
        Require(first.Decision.Recommended == Tier.Fast && first.Decision.Selected == Tier.Fast &&
            first.ActualRoute == "fast" && first.Feedback.Outcome == Outcome.Failure && first.Attempts.Count == 1,
            "Initial fast response must be a semantic failure, not a transport retry.");
        var reopened = new SqliteOutcomeStore(path);
        using var nextRouter = new OutcomeRouter(generator, catalog, settings, reopened);
        var nextApp = new OutcomeApplication(nextRouter, catalog, reopened);
        var next = await nextApp.ExecuteAsync(Task("sort-verified-v1"), verifier);
        Print("2 after persisted failure/reload", next);
        Require(next.Decision.Recommended == Tier.Fast && next.Decision.Selected == Tier.Balanced &&
            next.ActualRoute == "balanced" && next.Feedback.Outcome == Outcome.Success &&
            next.Decision.ProjectedState.Contains("\"Outcome\":\"Failure\"", StringComparison.Ordinal),
            "Verified failure must enter bounded state and raise the next request's minimum tier.");

        var unknown = await app.ExecuteAsync(Task("sort-unknown-v1"), new ExplicitUnknownVerifier());
        Print("3 explicit unknown", unknown);
        var afterUnknown = await nextApp.ExecuteAsync(Task("sort-unknown-v1"), null);
        Print("4 after unknown", afterUnknown);
        Require(unknown.Feedback.Outcome == Outcome.Unknown && afterUnknown.Decision.Selected == Tier.Fast &&
            reopened.ReadEvidence("sort-unknown-v1", catalog.Revision, settings).Count == 0,
            "Unknown and transport-only completion must not supply learning evidence.");

        using var failoverClients = ClientSet.Fixtures(FixtureBehavior.FailBeforeOutput);
        using var failoverRouter = new OutcomeRouter(generator, failoverClients.Catalog, settings, store);
        var failoverApp = new OutcomeApplication(failoverRouter, failoverClients.Catalog, store);
        var alternate = await failoverApp.ExecuteAsync(Task("sort-health-v1"), verifier);
        Print("5 pre-output failover", alternate);
        var evidence = reopened.ReadEvidence("sort-health-v1", failoverClients.Catalog.Revision, settings);
        Require(alternate.Attempts.Count == 2 && alternate.Attempts[0].ErrorType is not null &&
            !alternate.Attempts[0].ResponseCompleted && alternate.Attempts[1].ResponseCompleted &&
            alternate.ActualRoute == "balanced" && alternate.Feedback.Outcome == Outcome.Success &&
            evidence.Count == 1 && evidence[0].Route == "balanced",
            "Verification must be attributed to the actual completed alternate, not failed fast.");
        Require(router.PendingRequestCount == 0 && nextRouter.PendingRequestCount == 0 && failoverRouter.PendingRequestCount == 0,
            "Request routing state must be cleaned up.");
        Console.WriteLine("All offline transition, persistence, unknown and actual-route attribution assertions passed.");
        Console.WriteLine($"Isolated demo database: {Path.GetFullPath(path)}");
        Console.WriteLine("Fixture probabilities are not Julia accuracy/performance results. No weights or cloud calls were used.");
    }

    internal static void Print(string label, ExecutionResult run)
    {
        Console.WriteLine($"{label}: signal={DecisionPolicy.Id(run.Decision.Recommended)} " +
            $"distribution=[{string.Join(", ", run.Decision.Probabilities.Select(p => $"{p.Key}:{p.Value.ToString("G6", CultureInfo.InvariantCulture)}"))}] " +
            $"policy={DecisionPolicy.Id(run.Decision.Selected)} actual={run.ActualRoute} " +
            $"outcome={run.Feedback.Outcome} provenance={run.Feedback.Provenance}");
        Console.WriteLine($"  reasons={string.Join(", ", run.Decision.Reasons)}; " +
            $"attempts={string.Join(" -> ", run.Attempts.Select(a => $"{a.Route}:{(a.ErrorType is null ? "completed" : "provider-failed")}"))}");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Demo assertion failed: {message}");
    }
    private sealed class ExplicitUnknownVerifier : ITaskVerifier
    {
        public Feedback Verify(ChatResponse response) => Feedback.Unknown("verification-not-available");
    }
}

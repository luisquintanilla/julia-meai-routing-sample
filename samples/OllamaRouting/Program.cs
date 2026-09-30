using DecisionInference;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OutcomeRouting;
using RoutingSamples;

if (args is ["--help"])
{
    Console.WriteLine(OllamaSettings.Usage);
    return 0;
}

try
{
    var settings = OllamaSettings.Parse(args);
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));

    Console.WriteLine("SIMULATED decision signal. REAL Ollama chat responses; local models must already be installed.");

    using IDecisionGenerator decisions = new FixtureDecisionGenerator();
    using IChatClient fast = new OllamaApiClient(settings.Endpoint, settings.FastModel);
    using IChatClient balanced = new OllamaApiClient(settings.Endpoint, settings.BalancedModel);
    using IChatClient strong = new OllamaApiClient(settings.Endpoint, settings.StrongModel);

    var ollama = fast.GetService<IOllamaApiClient>()
        ?? throw new InvalidOperationException("Expected the OllamaSharp model inventory service.");
    await settings.EnsureModelsAvailableAsync(ollama, deadline.Token);

    ChatRoute[] routes =
    [
        ChatRoute.Create(Tier.Fast, fast),
        ChatRoute.Create(Tier.Balanced, balanced),
        ChatRoute.Create(Tier.Strong, strong)
    ];

    IOutcomeStore history = new SqliteOutcomeStore(
        Path.Combine(".routing", $"ollama-{Guid.NewGuid():N}.db"));
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

    if (settings.Stream)
    {
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            "Sort [3,1,2] ascending. Return only the JSON array.",
            cancellationToken: deadline.Token))
        {
            Console.Write(update.Text);
        }

        Console.WriteLine();
    }
    else
    {
        ChatResponse response = await chatClient.GetResponseAsync(
            "Sort [3,1,2] ascending. Return only the JSON array.",
            cancellationToken: deadline.Token);
        Console.WriteLine(response.Text);
    }

    return 0;
}
catch (Exception error)
{
    string explanation = error switch
    {
        ArgumentException => error.Message,
        OperationCanceledException => "The two-minute request deadline expired or execution was cancelled.",
        _ => "Ollama execution failed. Check that Ollama is running, the exact local model tags are installed, and the models accept the route settings. No fixture fallback was used."
    };

    Console.Error.WriteLine($"ERROR ({error.GetType().Name}): {explanation}");
    return 2;
}

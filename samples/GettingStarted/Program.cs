using DecisionInference;
using Microsoft.Extensions.AI;
using OutcomeRouting;
using RoutingSamples;

Console.WriteLine("SIMULATED decisions and chat responses. Real routing, verification and persistence.");

using IDecisionGenerator decisions = new FixtureDecisionGenerator();
using IChatClient fast = new DemoChatClient("demo-fast", "[3,1,2]");
using IChatClient balanced = new DemoChatClient("demo-balanced", "[1,2,3]");
using IChatClient strong = new DemoChatClient("demo-strong", "[1,2,3]");

ChatRoute[] routes =
[
    ChatRoute.Create(Tier.Fast, fast),
    ChatRoute.Create(Tier.Balanced, balanced),
    ChatRoute.Create(Tier.Strong, strong)
];

IOutcomeStore history = new SqliteOutcomeStore(
    Path.Combine(".routing", $"getting-started-{Guid.NewGuid():N}.db"));
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

ChatResponse first = await chatClient.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(first.Text);

ChatResponse next = await chatClient.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(next.Text);

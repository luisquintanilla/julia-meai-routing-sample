using Microsoft.Extensions.AI;
using OutcomeRouting;
using RoutingSamples;

Console.WriteLine("SIMULATED decisions and chat responses. Real routing, verification and persistence.");

using var decisions = new FixtureDecisionGenerator();
using var fast = new DemoChatClient("demo-fast", "[3,1,2]");
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
        HistoryPath = Path.Combine(".routing", $"getting-started-{Guid.NewGuid():N}.db"),
        Cohort = "sort-integers-v1",
        VerifierFactory = SortVerification.ForMessages
    })
    .AsBuilder()
    .Build();

ChatResponse first = await chatClient.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(first.Text);

ChatResponse next = await chatClient.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(next.Text);

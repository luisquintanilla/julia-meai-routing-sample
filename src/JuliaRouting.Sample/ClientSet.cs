using OutcomeRouting;

internal sealed class ClientSet(RouteCatalog catalog) : IDisposable
{
    internal RouteCatalog Catalog { get; } = catalog;

    internal static ClientSet Fixtures(FixtureBehavior fast)
    {
        var routes = Enum.GetValues<Tier>().Select(tier => new ChatRoute(
            DecisionPolicy.Id(tier),
            tier,
            Capability.Text | Capability.Json,
            "fixture://scripted",
            $"fixture-{DecisionPolicy.Id(tier)}-v1",
            tier == Tier.Fast ? $"scripted-{fast}" : "scripted-Correct",
            new ScriptedChatClient(tier == Tier.Fast ? fast : FixtureBehavior.Correct)));

        return new ClientSet(new RouteCatalog(routes));
    }

    public void Dispose()
    {
        foreach (var route in Catalog.Routes)
        {
            route.Client.Dispose();
        }
    }
}

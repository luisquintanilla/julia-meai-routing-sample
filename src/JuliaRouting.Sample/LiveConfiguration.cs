using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OutcomeRouting;

internal sealed record LiveConfiguration(
    Uri Endpoint,
    string ApiKey,
    string[] Models,
    string?[] Reasoning)
{
    internal static LiveConfiguration FromEnvironment()
    {
        var endpoint = new Uri(Require("ROUTING_CHAT_ENDPOINT"), UriKind.Absolute);

        bool secureScheme = endpoint.Scheme == Uri.UriSchemeHttps ||
            (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);

        if (!secureScheme || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "Use an HTTPS endpoint (HTTP only for loopback), without credentials/query/fragment.");
        }

        string[] tiers = ["FAST", "BALANCED", "STRONG"];
        var models = tiers.Select(tier => Require($"ROUTING_{tier}_MODEL")).ToArray();

        if (models.Any(model => model.Length > 64) || models.Distinct(StringComparer.Ordinal).Count() < 2)
        {
            throw new ArgumentException(
                "Supply 2+ distinct model IDs, each at most 64 characters, mapped to all three tiers.");
        }

        var reasoning = tiers
            .Select(tier => Environment.GetEnvironmentVariable($"ROUTING_{tier}_REASONING"))
            .ToArray();

        if (reasoning.Any(effort => effort is not (null or "low" or "medium" or "high")))
        {
            throw new ArgumentException("Configured reasoning effort must be low, medium or high, or absent.");
        }

        return new LiveConfiguration(endpoint, Require("ROUTING_CHAT_API_KEY"), models, reasoning);
    }

    internal ClientSet CreateClients()
    {
        // Disable SDK retries so MEAI's ledger reflects each route invocation.
        var openai = new OpenAIClient(
            new ApiKeyCredential(ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = Endpoint,
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)
            });

        List<ChatRoute> routes = [];

        foreach (var tier in Enum.GetValues<Tier>())
        {
            int index = (int)tier;
            IChatClient chatClient = openai.GetChatClient(Models[index]).AsIChatClient();

            routes.Add(new ChatRoute(
                name: DecisionPolicy.Id(tier),
                tier: tier,
                capabilities: Capability.Text | Capability.Json,
                providerEndpoint: Endpoint.AbsoluteUri,
                model: Models[index],
                configRevision: "openai-text-json-v1",
                client: chatClient,
                configuration: new RouteSettings(Reasoning: Reasoning[index])));
        }

        return new ClientSet(new RouteCatalog(routes));
    }

    private static string Require(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing live configuration: {name}.");
        }

        return value;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

public enum Tier
{
    Fast,
    Balanced,
    Strong
}

[Flags]
public enum Capability
{
    Text = 1,
    Json = 2
}

public enum Outcome
{
    Unknown,
    Success,
    Failure
}

public enum Provenance
{
    None,
    Verifier,
    Application
}

public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Abandoned
}

public sealed record RouteSettings(
    double Temperature = 0,
    int MaximumOutputTokens = 128,
    string? Reasoning = null)
{
    internal void Validate()
    {
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2 || MaximumOutputTokens is < 1 or > 4096 ||
            Reasoning is not (null or "low" or "medium" or "high"))
        {
            throw new ArgumentException("Invalid explicit route temperature, output budget or reasoning effort.");
        }
    }
}

public sealed record RoutingTask
{
    public Guid RunId { get; }
    public string Cohort { get; }
    public string Task { get; }
    public string Context { get; }
    public Capability Required { get; }
    public Tier MinimumTier { get; }

    public RoutingTask(
        string cohort,
        string task,
        string context = "",
        Capability required = Capability.Text,
        Tier minimumTier = Tier.Fast,
        Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guard.Key(cohort, nameof(cohort));
        Guard.Text(task, 300, nameof(task));
        if (context.Length > 120)
        {
            throw new ArgumentException("Context is limited to 120 characters.", nameof(context));
        }

        Guard.Capabilities(required);

        if (!Enum.IsDefined(minimumTier))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTier));
        }

        RunId = runId ?? Guid.NewGuid();

        if (RunId == Guid.Empty)
        {
            throw new ArgumentException("An empty run ID is invalid.", nameof(runId));
        }

        (Cohort, Task, Context, Required, MinimumTier) = (cohort, task, context, required, minimumTier);
    }
}

public sealed record Feedback
{
    public Outcome Outcome { get; }
    public Provenance Provenance { get; }
    public string Source { get; }

    public Feedback(Outcome outcome, Provenance provenance, string source)
    {
        if (!Enum.IsDefined(outcome) || !Enum.IsDefined(provenance))
        {
            throw new ArgumentException("Undefined outcome or provenance.");
        }

        Guard.Key(source, nameof(source));

        if ((outcome == Outcome.Unknown) != (provenance == Provenance.None))
        {
            throw new ArgumentException("Known outcomes require verifier/application provenance; unknown requires None.");
        }

        (Outcome, Provenance, Source) = (outcome, provenance, source);
    }

    public static Feedback Unknown(string source = "no-verifier") => new(Outcome.Unknown, Provenance.None, source);
}

public sealed class ChatRoute
{
    public string Name { get; }
    public Tier Tier { get; }
    public Capability Capabilities { get; }
    public string Model { get; }
    public string Identity { get; }
    internal string ExecutionIdentity { get; }
    public IChatClient Client { get; }

    /// <summary>
    /// Uses the client's advertised endpoint and default model. Providers without complete metadata
    /// must use the explicit constructor rather than guessing an evidence identity.
    /// </summary>
    public static ChatRoute Create(
        Tier tier,
        IChatClient client,
        string configRevision = "v1",
        Capability capabilities = Capability.Text,
        RouteSettings? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var metadata = client.GetService<ChatClientMetadata>();
        var endpoint = metadata?.ProviderUri;
        var model = metadata?.DefaultModelId;

        if (endpoint is null || !endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException(
                "Client metadata must supply an absolute ProviderUri and DefaultModelId. " +
                "Use the explicit ChatRoute constructor when the provider does not expose them.",
                nameof(client));
        }

        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("Route identity must not contain endpoint credentials, query or fragment.", nameof(client));
        }

        return new ChatRoute(
            DecisionPolicy.Id(tier),
            tier,
            capabilities,
            endpoint.AbsoluteUri,
            model,
            configRevision,
            client,
            configuration);
    }

    public ChatRoute(
        string name,
        Tier tier,
        Capability capabilities,
        string providerEndpoint,
        string model,
        string configRevision,
        IChatClient client,
        RouteSettings? configuration = null)
    {
        Guard.Key(name, nameof(name), 24);
        Guard.Text(providerEndpoint, 256, nameof(providerEndpoint));
        Guard.Text(model, 64, nameof(model));
        Guard.Key(configRevision, nameof(configRevision));
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier));
        }

        Guard.Capabilities(capabilities);
        ArgumentNullException.ThrowIfNull(client);
        configuration ??= new();
        configuration.Validate();
        (Name, Tier, Capabilities, Model) = (name, tier, capabilities, model);

        ExecutionIdentity = Fingerprint(JsonSerializer.Serialize(new { providerEndpoint, model, configuration }));

        Identity = Fingerprint(JsonSerializer.Serialize(new
        {
            name,
            tier,
            capabilities,
            providerEndpoint,
            model,
            configRevision,
            configuration
        }));

        Client = client.AsBuilder()
            .ConfigureOptions(options =>
            {
                // Application metadata must not cross the downstream provider boundary.
                options.AdditionalProperties?.Remove(OutcomeRouter.RequestKey);
                options.ModelId = model;
                options.Temperature = (float)configuration.Temperature;
                options.MaxOutputTokens = configuration.MaximumOutputTokens;
                options.Reasoning = configuration.Reasoning switch
                {
                    "low" => new() { Effort = ReasoningEffort.Low },
                    "medium" => new() { Effort = ReasoningEffort.Medium },
                    "high" => new() { Effort = ReasoningEffort.High },
                    _ => null
                };
            })
            .Build();
    }

    internal static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class RouteCatalog
{
    public IReadOnlyList<ChatRoute> Routes { get; }
    public string Revision { get; }

    public RouteCatalog(IEnumerable<ChatRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var copy = routes.ToArray();

        if (copy.Length is < 3 or > 6 || copy.Any(route => route is null) ||
            copy.Select(route => route.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            Enum.GetValues<Tier>().Any(tier => !copy.Any(route => route.Tier == tier)))
        {
            throw new ArgumentException("Configure 3-6 uniquely named routes covering fast/balanced/strong.", nameof(routes));
        }

        Routes = Array.AsReadOnly(copy);

        var identities = copy
            .OrderBy(route => route.Name, StringComparer.Ordinal)
            .Select(route => route.Identity);

        Revision = ChatRoute.Fingerprint(string.Join("\n", identities));
    }
}

public sealed record Evidence(
    string Route,
    string RouteIdentity,
    Tier Tier,
    Outcome Outcome,
    Provenance Provenance);

public sealed record DecisionSnapshot(
    string Model,
    Tier Recommended,
    IReadOnlyDictionary<string, double> Probabilities,
    Tier Selected,
    IReadOnlyList<string> Reasons,
    string ProjectedState,
    long? InputTokens);

public sealed record AttemptRecord(
    int Number,
    string Route,
    string RouteIdentity,
    Tier Tier,
    long DurationTicks,
    string? ErrorType,
    bool ResponseCompleted,
    bool OutputCommitted,
    long? FirstUpdateTicks);

public sealed record RunRecord(
    Guid RunId,
    string Cohort,
    string CatalogRevision,
    RunStatus Status,
    string? ActualRouteIdentity,
    Feedback? Feedback);

public sealed record ExecutionResult(
    Guid RunId,
    ChatResponse Response,
    DecisionSnapshot Decision,
    IReadOnlyList<AttemptRecord> Attempts,
    string ActualRoute,
    Feedback Feedback);

internal static class Guard
{
    internal static void Text(string value, int limit, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
        {
            throw new ArgumentException($"{name} must be nonempty and at most {limit} characters.", name);
        }
    }

    internal static void Key(string value, string name, int limit = 64)
    {
        Text(value, limit, name);

        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')))
        {
            throw new ArgumentException($"{name} must be an application-owned ASCII key.", name);
        }
    }

    internal static void Capabilities(Capability value)
    {
        if (value == 0 || (value & ~(Capability.Text | Capability.Json)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Only declared Text/Json capabilities are supported.");
        }
    }
}

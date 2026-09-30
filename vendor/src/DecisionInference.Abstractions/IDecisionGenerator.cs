using Microsoft.Extensions.AI;

namespace DecisionInference;

/// <summary>Evaluates a complete batch of typed decisions against owned JSON state.</summary>
/// <remarks>
/// Concurrency and cancellation guarantees depend on the provider. Local synchronous inference is
/// not made nonblocking by this task-based contract. Do not dispose a generator or borrowed resources
/// while using them; consult the provider's ownership and in-flight cancellation limitations.
/// </remarks>
public interface IDecisionGenerator : IDisposable
{
    /// <summary>Returns fully correlated, validated answers or fails the entire input batch.</summary>
    /// <remarks>Providers must not mutate caller options. Callers must not mutate options during a call.</remarks>
    Task<DecisionResult> GenerateAsync(DecisionInput input, DecisionGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
    /// <summary>Retrieves a service for the exact type and key, or null when unavailable.</summary>
    object? GetService(Type serviceType, object? serviceKey = null);
}

public enum DecisionKind { Binary, Choice, Score }

/// <summary>Per-call model selection and provider-specific extension properties.</summary>
public class DecisionGenerationOptions
{
    public DecisionGenerationOptions() { }

    /// <summary>Copies options, shallow-cloning the additional-properties dictionary if present.</summary>
    protected DecisionGenerationOptions(DecisionGenerationOptions? other)
    {
        ModelId = other?.ModelId;
        AdditionalProperties = other?.AdditionalProperties?.Clone();
    }

    /// <summary>Gets or sets the requested model; null uses the generator's default.</summary>
    public string? ModelId { get; set; }
    /// <summary>Gets or sets extension properties. Support is provider-specific; current providers recognize no keys.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }

    /// <summary>Creates a shallow copy with a distinct additional-properties dictionary.</summary>
    /// <remarks>Dictionary values remain shared references. Derived options should override this method.</remarks>
    public virtual DecisionGenerationOptions Clone() => new(this);
}

/// <summary>Describes a generator; metadata does not perform input validation.</summary>
public sealed class DecisionGeneratorMetadata
{
    public string? ProviderName { get; }
    public Uri? ProviderUri { get; }
    /// <summary>Gets the default model, unaffected by per-call overrides.</summary>
    public string? DefaultModelId { get; }
    /// <summary>Gets an owned capability snapshot: null means unknown, empty means none supported.</summary>
    public IReadOnlyList<DecisionKind>? SupportedKinds { get; }

    public DecisionGeneratorMetadata(string? providerName = null, Uri? providerUri = null,
        string? defaultModelId = null, IEnumerable<DecisionKind>? supportedKinds = null)
    {
        ProviderName = providerName;
        ProviderUri = providerUri;
        DefaultModelId = defaultModelId;
        if (supportedKinds is not null)
        {
            var copy = supportedKinds.Distinct().ToArray();
            if (copy.Any(k => !Enum.IsDefined(k)))
                throw new ArgumentException("Capabilities must be defined decision kinds.", nameof(supportedKinds));
            SupportedKinds = Array.AsReadOnly(copy);
        }
    }
}

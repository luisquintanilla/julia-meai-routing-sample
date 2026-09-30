using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DecisionInference;

/// <summary>Service lookup and explicit, metadata-based state serialization conveniences.</summary>
public static class DecisionGeneratorExtensions
{
    /// <summary>Gets a service for the exact key, or the default value when unavailable or of another type.</summary>
    public static TService? GetService<TService>(this IDecisionGenerator generator, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator.GetService(typeof(TService), serviceKey) is TService service ? service : default;
    }

    /// <summary>Gets a required service for the exact key, or throws <see cref="InvalidOperationException"/>.</summary>
    public static object GetRequiredService(this IDecisionGenerator generator, Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(serviceType);
        var service = generator.GetService(serviceType, serviceKey);
        return service is not null && serviceType.IsInstanceOfType(service) ? service :
            throw new InvalidOperationException($"A service of type '{serviceType}' is not available for the specified key.");
    }

    /// <summary>Gets a required typed service for the exact key, or throws <see cref="InvalidOperationException"/>.</summary>
    public static TService GetRequiredService<TService>(this IDecisionGenerator generator, object? serviceKey = null) =>
        (TService)generator.GetRequiredService(typeof(TService), serviceKey);

    /// <summary>Snapshots JSON state and questions, forwarding options and cancellation unchanged.</summary>
    public static Task<DecisionResult> GenerateAsync(this IDecisionGenerator generator, JsonElement state,
        IEnumerable<DecisionQuestion> questions, DecisionGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator.GenerateAsync(new DecisionInput(state, questions), options, cancellationToken);
    }

    /// <summary>Serializes state using only the supplied metadata, then evaluates the questions.</summary>
    /// <remarks>Null state is serialized according to the metadata. No reflection fallback or hidden serializer configuration is used.</remarks>
    public static Task<DecisionResult> GenerateAsync<TState>(this IDecisionGenerator generator, TState state,
        IEnumerable<DecisionQuestion> questions, JsonTypeInfo<TState> stateTypeInfo,
        DecisionGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(stateTypeInfo);
        cancellationToken.ThrowIfCancellationRequested();
        return generator.GenerateAsync(JsonSerializer.SerializeToElement(state, stateTypeInfo), questions, options, cancellationToken);
    }
}

using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

/// <summary>
/// A sample-owned IChatClient using real MEAI failover and independently known outcomes.
/// The generator and route clients remain caller-owned.
/// </summary>
public sealed class OutcomeRoutingChatClient : IChatClient
{
    public const string ResponseInfoKey = "OutcomeRouting.Result";

    private readonly OutcomeRouter _router;
    private readonly OutcomeApplication _application;
    private readonly IOutcomeStore _store;
    private readonly OutcomeRoutingOptions _configuration;
    private readonly ChatClientMetadata _metadata = new("outcome-routing");
    private int _disposed;

    public OutcomeRoutingChatClient(
        IDecisionGenerator decisions,
        IEnumerable<ChatRoute> routes,
        OutcomeRoutingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var catalog = new RouteCatalog(routes);
        _configuration = options ?? new OutcomeRoutingOptions();
        _configuration.Validate();

        _store = _configuration.History ?? new SqliteOutcomeStore(_configuration.HistoryPath ?? @".routing\history.db");
        _router = new OutcomeRouter(decisions, catalog, _configuration.Policy, _store, _configuration.Observer);
        _application = new OutcomeApplication(_router, catalog, _store);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = Prepare(messages, options, cancellationToken);
        return RespondAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Snapshot now, not when enumeration starts; single-use sequences are supported.
        var request = Prepare(messages, options, cancellationToken);
        return StreamAsync(request, cancellationToken);
    }

    private async Task<ChatResponse> RespondAsync(Request request, CancellationToken cancellationToken)
    {
        var task = CreateTask(request);
        var verifier = _configuration.VerifierFactory?.Invoke(request.Messages);
        var result = await _application.ExecuteAsync(
            task,
            request.Messages,
            verifier,
            request.Options,
            cancellationToken);

        var response = result.Response;
        response.AdditionalProperties ??= [];

        if (!response.AdditionalProperties.TryAdd(ResponseInfoKey, RoutingResponseInfo.From(result)))
        {
            throw new InvalidOperationException($"The provider response uses the reserved {ResponseInfoKey} key.");
        }

        return response;
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        Request request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var task = CreateTask(request);
        var verifier = _configuration.VerifierFactory?.Invoke(request.Messages);

        await foreach (var update in _application.StreamAsync(
            task,
            request.Messages,
            verifier,
            request.Options,
            cancellationToken))
        {
            // Forward the actual provider updates; do not inject telemetry into the stream.
            yield return update;
        }
    }

    private Request Prepare(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(messages);

        var snapshot = Array.AsReadOnly(messages.ToArray());
        var callerOptions = options?.Clone();
        OutcomeApplication.ValidateOptions(callerOptions, _configuration.RequiredCapabilities);

        if (snapshot.Count == 0)
        {
            throw new ArgumentException("Supply at least one text message.", nameof(messages));
        }

        foreach (var message in snapshot)
        {
            if (message is null ||
                (message.Role != ChatRole.System && message.Role != ChatRole.User && message.Role != ChatRole.Assistant) ||
                message.Contents.Count == 0 ||
                message.Contents.Any(content => content is not TextContent))
            {
                throw new NotSupportedException(
                    "Only system, user and assistant text messages are supported; images, tools and opaque content are not.");
            }
        }

        // The complete text/role history informs selection. Its strict budget never truncates
        // or changes the original messages forwarded to the selected provider.
        string projection = snapshot.Count == 1 && snapshot[0].Role == ChatRole.User
            ? snapshot[0].Text
            : string.Join("\n", snapshot.Select(message => $"{message.Role}: {message.Text}"));

        Guard.Text(projection, 300, nameof(messages));
        return new Request(projection, snapshot, callerOptions);
    }

    private RoutingTask CreateTask(Request request) => new(
        _configuration.Cohort,
        request.Projection,
        required: _configuration.RequiredCapabilities,
        minimumTier: _configuration.MinimumTier);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            // A multi-provider client has no single provider URI or default model.
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public RunRecord GetRun(Guid runId)
    {
        ThrowIfDisposed();
        return _store.GetRun(runId);
    }

    public IReadOnlyList<AttemptRecord> GetAttempts(Guid runId)
    {
        ThrowIfDisposed();
        return _store.GetAttempts(runId);
    }

    public bool ReportFeedback(Guid runId, Feedback feedback)
    {
        ThrowIfDisposed();
        var run = _store.GetRun(runId);

        if (run.Status != RunStatus.Completed || run.ActualRouteIdentity is null)
        {
            throw new InvalidOperationException("Feedback requires a completed local run.");
        }

        return _store.ReportFeedback(runId, run.ActualRouteIdentity, feedback);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _router.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record Request(
        string Projection,
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions? Options);
}

public sealed record OutcomeRoutingOptions
{
    public string? HistoryPath { get; init; }

    /// <summary>Optional caller-owned history abstraction. Cannot be combined with HistoryPath.</summary>
    public IOutcomeStore? History { get; init; }
    public string Cohort { get; init; } = "text-v1";
    public Capability RequiredCapabilities { get; init; } = Capability.Text;
    public Tier MinimumTier { get; init; } = Tier.Fast;
    public PolicySettings Policy { get; init; } = new();
    public IRoutingObserver? Observer { get; init; }

    /// <summary>
    /// Creates an independent check for these exact messages. Null means unknown quality.
    /// The callback and returned verifiers must support the caller's concurrency.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, ITaskVerifier?>? VerifierFactory { get; init; }

    internal void Validate()
    {
        if (History is not null && HistoryPath is not null)
        {
            throw new ArgumentException("Choose History or HistoryPath, not both.");
        }

        if (HistoryPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(HistoryPath);
        }
        Guard.Key(Cohort, nameof(Cohort));
        Guard.Capabilities(RequiredCapabilities);

        if (!Enum.IsDefined(MinimumTier))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumTier));
        }

        ArgumentNullException.ThrowIfNull(Policy);
        Policy.Validate();
    }
}

/// <summary>Optional immutable routing information, without raw messages or projected task state.</summary>
public sealed record RoutingResponseInfo(
    Guid RunId,
    string DecisionModel,
    Tier RecommendedTier,
    Tier SelectedTier,
    IReadOnlyDictionary<string, double> Probabilities,
    IReadOnlyList<string> Reasons,
    string ActualRoute,
    IReadOnlyList<AttemptRecord> Attempts,
    Feedback Feedback)
{
    internal static RoutingResponseInfo From(ExecutionResult result) => new(
        result.RunId,
        result.Decision.Model,
        result.Decision.Recommended,
        result.Decision.Selected,
        new ReadOnlyDictionary<string, double>(result.Decision.Probabilities.ToDictionary()),
        Array.AsReadOnly(result.Decision.Reasons.ToArray()),
        result.ActualRoute,
        Array.AsReadOnly(result.Attempts.ToArray()),
        result.Feedback);
}

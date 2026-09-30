using System.Collections.Concurrent;
using DecisionInference;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

internal sealed class RequestSession(RoutingTask task)
{
    internal RoutingTask Task { get; } = task;
    internal DecisionSnapshot? Decision { get; set; }
    internal List<AttemptRecord> Attempts { get; } = [];
    internal HashSet<string> Invoked { get; } = new(StringComparer.Ordinal);
    internal ChatRoute? Current { get; set; }
    internal ChatRoute? Completed { get; set; }
}

// Only this implementation uses MEAI's experimental routing API.
#pragma warning disable MEAI001
public sealed class OutcomeRouter : FailoverChatClient
{
    internal const string RequestKey = "outcome-routing-request";
    private readonly ConcurrentDictionary<RoutingContext, RequestSession> _pending = new();
    private readonly RouteCatalog _catalog;
    private readonly PolicySettings _settings;
    private readonly DecisionPolicy _policy;
    private readonly IOutcomeStore _store;

    public int PendingRequestCount => _pending.Count;

    public OutcomeRouter(
        IDecisionGenerator generator,
        RouteCatalog catalog,
        PolicySettings settings,
        IOutcomeStore store)
    {
        settings.Validate();
        (_catalog, _settings, _store) = (catalog, settings, store);
        _policy = new(generator, catalog, settings);
        MaximumAttemptsPerRequest = settings.MaximumAttempts;
    }

    protected override async ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _pending.GetOrAdd(context, static request =>
            {
                if (request.ChatOptions?.AdditionalProperties?.TryGetValue(RequestKey, out var value) == true &&
                    value is RequestSession session)
                {
                    return session;
                }

                throw new InvalidOperationException("Use OutcomeApplication to supply a durable, application-owned request.");
            });

            if (session.Decision is null)
            {
                var evidence = _store.ReadEvidence(session.Task.Cohort, _catalog.Revision, _settings);
                session.Decision = await _policy.DecideAsync(session.Task, evidence, cancellationToken);
                _store.RecordDecision(session.Task.RunId, session.Decision);
            }

            var route = _catalog.Routes
                .Where(route =>
                    route.Tier >= session.Decision.Selected &&
                    (route.Capabilities & session.Task.Required) == session.Task.Required &&
                    !session.Invoked.Contains(route.ExecutionIdentity))
                .OrderBy(route => route.Tier)
                .ThenBy(route => route.Name, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No untried eligible route remains.");

            session.Invoked.Add(route.ExecutionIdentity);
            session.Current = route;
            return route.Client;
        }
        catch
        {
            _pending.TryRemove(context, out _);
            throw;
        }
    }

    protected override ValueTask OnRoutingUpdateAsync(
        RoutingContext context,
        FailoverChatClientAttempt attempt,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = _pending[context];
            var route = session.Current ?? throw new InvalidOperationException("Attempt without a selected route.");

            if (!ReferenceEquals(route.Client, attempt.Client))
            {
                throw new InvalidOperationException("Attempt attribution does not match the invoked route.");
            }

            var record = new AttemptRecord(
                session.Attempts.Count + 1,
                route.Name,
                route.Identity,
                route.Tier,
                attempt.Duration.Ticks,
                attempt.Exception?.GetType().FullName,
                attempt.ResponseCompleted,
                attempt.OutputCommitted,
                attempt.TimeToFirstUpdate?.Ticks);

            // Persist cancellation/abandonment telemetry too; the canceled request token must not cancel cleanup.
            _store.RecordAttempt(session.Task.RunId, record);
            session.Attempts.Add(record);
            if (attempt.ResponseCompleted)
            {
                session.Completed = route;
            }

            if (isTerminal)
            {
                _pending.TryRemove(context, out _);
            }

            return ValueTask.CompletedTask;
        }
        catch
        {
            _pending.TryRemove(context, out _);
            throw;
        }
    }

    // Route wrappers, underlying clients, the generator and the store are owned by the application.
}
#pragma warning restore MEAI001

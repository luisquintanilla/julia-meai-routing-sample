using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

public interface ITaskVerifier
{
    Feedback Verify(ChatResponse response);
}

/// <summary>Checks a task result against independent input-derived expectations, never another model's judgment.</summary>
public sealed class ExactSortVerifier : ITaskVerifier
{
    private readonly int[] _expected;
    public ExactSortVerifier(IEnumerable<int> input) => _expected = input.Order().ToArray();

    public Feedback Verify(ChatResponse response)
    {
        if (response.Messages.Count != 1 || response.Messages[0].Role != ChatRole.Assistant ||
            response.Messages[0].Contents.Any(c => c is not TextContent))
            return Feedback.Unknown("unsupported-response");
        try
        {
            using var json = JsonDocument.Parse(response.Text);
            bool matches = json.RootElement.ValueKind == JsonValueKind.Array &&
                json.RootElement.GetArrayLength() == _expected.Length &&
                json.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? (int?)n : null)
                    .SequenceEqual(_expected.Select(n => (int?)n));
            return new(matches ? Outcome.Success : Outcome.Failure, Provenance.Verifier, "exact-sort-v1");
        }
        catch (JsonException)
        {
            return new(Outcome.Failure, Provenance.Verifier, "exact-sort-v1");
        }
    }
}

public sealed class OutcomeApplication(OutcomeRouter router, RouteCatalog catalog, IOutcomeStore store)
{
    public async Task<ExecutionResult> ExecuteAsync(RoutingTask task, ITaskVerifier? verifier,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Begin(task);
        ChatResponse response;
        try
        {
            response = await router.GetResponseAsync(Messages(task), Options(options, session), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            FinishFailure(session, cancellationToken.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Failed, error);
            throw;
        }
        var actual = session.Completed ?? throw new InvalidOperationException("Response without a completed route.");
        Feedback feedback;
        try
        {
            feedback = verifier is null ? Feedback.Unknown() :
                verifier.Verify(response) ?? throw new InvalidOperationException("Verifier returned null feedback.");
        }
        catch (Exception error)
        {
            try { store.Finish(task.RunId, RunStatus.Completed, actual.Identity, null); }
            catch (Exception storageError) { throw new AggregateException("Verifier and terminal persistence failed.", error, storageError); }
            throw;
        }
        store.Finish(task.RunId, RunStatus.Completed, actual.Identity, verifier is null ? null : feedback);
        return new(task.RunId, response, session.Decision!, session.Attempts.AsReadOnly(), actual.Name, feedback);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(RoutingTask task, ITaskVerifier? verifier,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Begin(task);
        bool fullyEnumerated = false, supportedText = true;
        Exception? failure = null;
        var text = new StringBuilder();
        try
        {
            IAsyncEnumerator<ChatResponseUpdate> iterator;
            try
            {
                iterator = router.GetStreamingResponseAsync(Messages(task), Options(options, session), cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { failure = error; throw; }
            await using var ownedIterator = iterator;
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await iterator.MoveNextAsync()) { fullyEnumerated = true; break; }
                    update = iterator.Current;
                    cancellationToken.ThrowIfCancellationRequested();
                    supportedText &= (update.Role is null || update.Role == ChatRole.Assistant) &&
                        update.Contents.All(c => c is TextContent);
                    text.Append(update.Text);
                    if (text.Length > 16_384) throw new InvalidOperationException("Sample streaming accumulation exceeds 16,384 characters.");
                }
                catch (Exception error) { failure = error; throw; }
                yield return update;
            }
        }
        finally
        {
            if (fullyEnumerated && session.Completed is { } actual && !cancellationToken.IsCancellationRequested)
            {
                Feedback feedback;
                try
                {
                    feedback = text.Length == 0 ? Feedback.Unknown("empty-stream") :
                        !supportedText ? Feedback.Unknown("unsupported-stream") :
                        verifier is null ? Feedback.Unknown() :
                        verifier.Verify(new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))) ??
                            throw new InvalidOperationException("Verifier returned null feedback.");
                }
                catch (Exception error)
                {
                    try { store.Finish(task.RunId, RunStatus.Completed, actual.Identity, null); }
                    catch (Exception storageError) { throw new AggregateException("Verifier and terminal persistence failed.", error, storageError); }
                    throw;
                }
                store.Finish(task.RunId, RunStatus.Completed, actual.Identity,
                    verifier is null && text.Length > 0 && supportedText ? null : feedback);
            }
            else
            {
                var status = cancellationToken.IsCancellationRequested ? RunStatus.Cancelled :
                    failure is not null ? RunStatus.Failed : RunStatus.Abandoned;
                FinishFailure(session, status, failure);
            }
        }
    }

    private RequestSession Begin(RoutingTask task)
    {
        store.Begin(task, catalog.Revision);
        return new(task);
    }

    private void FinishFailure(RequestSession session, RunStatus status, Exception? error)
    {
        try { store.Finish(session.Task.RunId, status, null, null); }
        catch (Exception storageError) when (error is not null)
        {
            throw new AggregateException("Request and terminal persistence failed.", error, storageError);
        }
    }

    private static ChatMessage[] Messages(RoutingTask task) =>
        [new(ChatRole.User, string.IsNullOrEmpty(task.Context) ? task.Task : $"{task.Task}\nContext: {task.Context}")];

    private static ChatOptions Options(ChatOptions? caller, RequestSession session)
    {
        if (caller?.ConversationId is not null || caller?.ContinuationToken is not null ||
            caller?.AllowBackgroundResponses == true || caller?.Tools?.Count > 0 ||
            caller?.ToolMode is not null || caller?.AllowMultipleToolCalls is not null)
            throw new ArgumentException("This stateless text/JSON sample does not support provider continuations, background responses or tools.", nameof(caller));
        if (caller?.ResponseFormat is not null && (session.Task.Required & Capability.Json) == 0)
            throw new ArgumentException("Response-format options require a declared Json task capability.", nameof(caller));
        if (caller?.AdditionalProperties?.ContainsKey(OutcomeRouter.RequestKey) == true)
            throw new ArgumentException("The routing request key is reserved.", nameof(caller));
        var options = caller?.Clone() ?? new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[OutcomeRouter.RequestKey] = session;
        return options;
    }
}

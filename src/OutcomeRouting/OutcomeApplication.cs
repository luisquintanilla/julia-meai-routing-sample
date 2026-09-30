using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

public sealed class OutcomeApplication(OutcomeRouter router, RouteCatalog catalog, IOutcomeStore store)
{
    public Task<ExecutionResult> ExecuteAsync(
        RoutingTask task,
        ITaskVerifier? verifier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(task, Messages(task), verifier, options, cancellationToken);
    }

    internal async Task<ExecutionResult> ExecuteAsync(
        RoutingTask task,
        IReadOnlyList<ChatMessage> messages,
        ITaskVerifier? verifier,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Begin(task);
        ChatResponse response;

        try
        {
            response = await router.GetResponseAsync(messages, Options(options, session), cancellationToken);
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
            feedback = Verify(response, verifier);
        }
        catch (Exception error)
        {
            FinishWithoutFeedback(task.RunId, actual.Identity, error);
            throw;
        }

        store.Finish(task.RunId, RunStatus.Completed, actual.Identity, verifier is null ? null : feedback);

        return new ExecutionResult(
            task.RunId,
            response,
            session.Decision!,
            session.Attempts.AsReadOnly(),
            actual.Name,
            feedback);
    }

    public IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        RoutingTask task,
        ITaskVerifier? verifier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamAsync(task, Messages(task), verifier, options, cancellationToken);
    }

    internal async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        RoutingTask task,
        IReadOnlyList<ChatMessage> messages,
        ITaskVerifier? verifier,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Begin(task);
        bool fullyEnumerated = false;
        bool supportedText = true;
        Exception? failure = null;
        var text = new StringBuilder();

        try
        {
            IAsyncEnumerator<ChatResponseUpdate> iterator;
            try
            {
                iterator = router.GetStreamingResponseAsync(messages, Options(options, session), cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }

            await using var ownedIterator = iterator;

            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await iterator.MoveNextAsync())
                    {
                        fullyEnumerated = true;
                        break;
                    }

                    update = iterator.Current;
                    cancellationToken.ThrowIfCancellationRequested();
                    supportedText &= (update.Role is null || update.Role == ChatRole.Assistant) &&
                        update.Contents.All(content => content is TextContent or UsageContent);
                    text.Append(update.Text);

                    if (text.Length > 16_384)
                    {
                        throw new InvalidOperationException("Sample streaming accumulation exceeds 16,384 characters.");
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    throw;
                }

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
                    if (text.Length == 0)
                    {
                        feedback = Feedback.Unknown("empty-stream");
                    }
                    else if (!supportedText)
                    {
                        feedback = Feedback.Unknown("unsupported-stream");
                    }
                    else
                    {
                        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()));
                        feedback = Verify(response, verifier);
                    }
                }
                catch (Exception error)
                {
                    FinishWithoutFeedback(task.RunId, actual.Identity, error);
                    throw;
                }

                bool leaveFeedbackOpen = verifier is null && text.Length > 0 && supportedText;

                store.Finish(
                    task.RunId,
                    RunStatus.Completed,
                    actual.Identity,
                    leaveFeedbackOpen ? null : feedback);
            }
            else
            {
                RunStatus status;
                if (cancellationToken.IsCancellationRequested)
                {
                    status = RunStatus.Cancelled;
                }
                else if (failure is not null)
                {
                    status = RunStatus.Failed;
                }
                else
                {
                    status = RunStatus.Abandoned;
                }

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
        try
        {
            store.Finish(session.Task.RunId, status, null, null);
        }
        catch (Exception storageError) when (error is not null)
        {
            throw new AggregateException("Request and terminal persistence failed.", error, storageError);
        }
    }

    private void FinishWithoutFeedback(Guid runId, string actualRouteIdentity, Exception verifierError)
    {
        try
        {
            store.Finish(runId, RunStatus.Completed, actualRouteIdentity, null);
        }
        catch (Exception storageError)
        {
            throw new AggregateException("Verifier and terminal persistence failed.", verifierError, storageError);
        }
    }

    private static Feedback Verify(ChatResponse response, ITaskVerifier? verifier)
    {
        if (verifier is null)
        {
            return Feedback.Unknown();
        }

        return verifier.Verify(response) ?? throw new InvalidOperationException("Verifier returned null feedback.");
    }

    private static ChatMessage[] Messages(RoutingTask task)
    {
        string prompt = string.IsNullOrEmpty(task.Context) ? task.Task : $"{task.Task}\nContext: {task.Context}";
        return [new ChatMessage(ChatRole.User, prompt)];
    }

    private static ChatOptions Options(ChatOptions? caller, RequestSession session)
    {
        ValidateOptions(caller, session.Task.Required);

        var options = caller?.Clone() ?? new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[OutcomeRouter.RequestKey] = session;
        return options;
    }

    internal static void ValidateOptions(ChatOptions? caller, Capability required)
    {
        if (caller?.ConversationId is not null || caller?.ContinuationToken is not null ||
            caller?.AllowBackgroundResponses == true || caller?.Tools?.Count > 0 ||
            caller?.ToolMode is not null || caller?.AllowMultipleToolCalls is not null)
        {
            throw new ArgumentException("This stateless text/JSON sample does not support provider continuations, background responses or tools.", nameof(caller));
        }

        if (caller?.ResponseFormat is not null && (required & Capability.Json) == 0)
        {
            throw new ArgumentException("Response-format options require a declared Json task capability.", nameof(caller));
        }

        if (caller?.AdditionalProperties?.ContainsKey(OutcomeRouter.RequestKey) == true)
        {
            throw new ArgumentException("The routing request key is reserved.", nameof(caller));
        }
    }
}

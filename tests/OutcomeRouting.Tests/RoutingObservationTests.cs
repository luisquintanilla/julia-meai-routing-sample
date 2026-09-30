using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Extensions.AI;
using RoutingSamples;

namespace OutcomeRouting.Tests;

public sealed class RoutingObservationTests
{
    [Fact]
    public async Task AbsentObserver_ReturnsOriginalResponseAndOnlyProviderStreamUpdates()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"));
        var update = new ChatResponseUpdate(ChatRole.Assistant, "[1,2,3]");
        using var provider = new DelegateClient(
            (_, _, _) => Task.FromResult(response),
            token => Updates([update], token));
        using var routing = Create(database, decisions, provider);

        Assert.Null(new OutcomeRoutingOptions().Observer);
        Assert.Same(response, await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        Assert.NotNull(response.AdditionalProperties);
        Assert.Single(response.AdditionalProperties);
        Assert.True(response.AdditionalProperties.ContainsKey(OutcomeRoutingChatClient.ResponseInfoKey));

        List<ChatResponseUpdate> received = [];
        await foreach (var item in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
        {
            received.Add(item);
        }

        Assert.Same(update, Assert.Single(received));
        Assert.Null(update.AdditionalProperties);
    }

    [Fact]
    public async Task Observation_PrecedesProviderAndCorrelatesDecisionPolicyAttemptAndFeedback()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var observer = new RecordingRoutingObserver();
        using var provider = new DelegateClient((_, _, _) =>
        {
            var selection = Assert.Single(observer.Snapshot());
            Assert.Equal(RoutingObservationPhase.RouteSelected, selection.Phase);
            Assert.Null(selection.Attempt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]")));
        });
        using var routing = Create(database, decisions, provider, observer);

        var info = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var records = observer.Snapshot();

        Assert.Equal(2, records.Count);
        Assert.Equal(
            new[] { RoutingObservationPhase.RouteSelected, RoutingObservationPhase.AttemptFinished },
            records.Select(record => record.Phase));
        Assert.All(records, record =>
        {
            Assert.Equal(info.RunId, record.RunId);
            Assert.Equal(info.DecisionModel, record.DecisionModel);
            Assert.Equal(info.RecommendedTier, record.RecommendedTier);
            Assert.Equal(info.SelectedTier, record.SelectedTier);
            Assert.Equal(info.Probabilities.OrderBy(pair => pair.Key), record.Probabilities.OrderBy(pair => pair.Key));
            Assert.Equal(info.Reasons, record.Reasons);
            Assert.Equal(info.ActualRoute, record.Route);
            Assert.Equal(Tier.Fast, record.RouteTier);
            Assert.Equal(info.Attempts[0].RouteIdentity, record.RouteIdentity);
        });
        Assert.Equal(info.Attempts[0], records[1].Attempt);
        Assert.True(records[1].Attempt!.ResponseCompleted);
        Assert.Equal(TestData.Success, routing.GetRun(info.RunId).Feedback);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, double>)records[0].Probabilities).Add("other", 1));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)records[0].Reasons).Clear());
        Assert.DoesNotContain(typeof(RoutingObservation).GetProperties(), property =>
            property.Name is "Task" or "Messages" or "ProjectedState" or "Response" or "Feedback");
        Assert.Throws<NotSupportedException>(() => ((IList<RoutingObservation>)records).Clear());
    }

    [Fact]
    public async Task EnabledObserver_ForwardsOriginalResponseAndUpdatesAndDoesNotInventKnownQuality()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var observer = new RecordingRoutingObserver();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"));
        ChatResponseUpdate[] updates =
        [
            new(ChatRole.Assistant, "[1,"),
            new(ChatRole.Assistant, "2,3]")
        ];
        using var provider = new DelegateClient(
            (_, _, _) => Task.FromResult(response),
            token => Updates(updates, token));
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(provider),
            new OutcomeRoutingOptions
            {
                History = new SqliteOutcomeStore(database.Path),
                Observer = observer
            });

        Assert.Same(response, await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var info = Info(response);
        Assert.Equal(Outcome.Unknown, info.Feedback.Outcome);
        Assert.Null(routing.GetRun(info.RunId).Feedback);
        Assert.Equal(2, observer.Snapshot().Count);

        List<ChatResponseUpdate> received = [];
        await foreach (var update in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
        {
            received.Add(update);
        }

        Assert.Equal(2, received.Count);
        Assert.Same(updates[0], received[0]);
        Assert.Same(updates[1], received[1]);
        Assert.All(received, update => Assert.Null(update.AdditionalProperties));
        var records = observer.Snapshot();
        Assert.Equal(4, records.Count);
        Assert.NotEqual(records[0].RunId, records[2].RunId);
        Assert.Null(routing.GetRun(records[2].RunId).Feedback);
        Assert.True(routing.ReportFeedback(
            info.RunId,
            new Feedback(Outcome.Failure, Provenance.Application, "independent-check")));
    }

    [Fact]
    public async Task PersistedFailure_ObservationDistinguishesRecommendationFromNextPolicyAndActualRoute()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new ScriptedChatClient(FixtureBehavior.Incorrect);
        var observer = new RecordingRoutingObserver();
        using var routing = Create(database, decisions, fast, observer);

        var first = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var next = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var records = observer.Snapshot();

        Assert.Equal(4, records.Count);
        Assert.Equal(Outcome.Failure, first.Feedback.Outcome);
        Assert.Equal(first.RunId, records[0].RunId);
        Assert.Equal(Tier.Fast, records[0].RecommendedTier);
        Assert.Equal(Tier.Fast, records[0].SelectedTier);
        Assert.Equal("fast", records[1].Route);
        Assert.Equal(next.RunId, records[2].RunId);
        Assert.Equal(Tier.Fast, records[2].RecommendedTier);
        Assert.Equal(Tier.Balanced, records[2].SelectedTier);
        Assert.Equal("balanced", records[3].Route);
        Assert.Contains("recent-verified-failure:fast:minimum-balanced", records[2].Reasons);
        Assert.Equal(Outcome.Success, next.Feedback.Outcome);
        Assert.Equal(1, fast.Invocations);
    }

    [Fact]
    public async Task Failover_ObservationReportsActualAlternateWithoutChangingOriginalDecision()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new ScriptedChatClient(FixtureBehavior.FailBeforeOutput);
        var observer = new RecordingRoutingObserver();
        using var routing = Create(database, decisions, fast, observer);

        var info = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var records = observer.Snapshot();

        Assert.Equal(4, records.Count);
        Assert.Equal(new[] { "fast", "fast", "balanced", "balanced" }, records.Select(record => record.Route));
        Assert.All(records, record =>
        {
            Assert.Equal(info.RunId, record.RunId);
            Assert.Equal(Tier.Fast, record.RecommendedTier);
            Assert.Equal(Tier.Fast, record.SelectedTier);
        });
        Assert.Equal(info.Attempts[0], records[1].Attempt);
        Assert.False(records[1].Attempt!.ResponseCompleted);
        Assert.Equal(info.Attempts[1], records[3].Attempt);
        Assert.True(records[3].Attempt!.ResponseCompleted);
        Assert.Equal(info.ActualRoute, records[3].Route);
        Assert.Equal(records[3].RouteIdentity, routing.GetRun(info.RunId).ActualRouteIdentity);
        Assert.Equal(TestData.Success, routing.GetRun(info.RunId).Feedback);
    }

    [Fact]
    public async Task ConcurrentObservations_KeepIndependentRunDecisionsAndActualAttempts()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var observer = new RecordingRoutingObserver();
        const int count = 8;
        int started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fast = new DelegateClient(async (messages, _, token) =>
        {
            string prompt = messages.Single().Text;
            if (Interlocked.Increment(ref started) == count)
            {
                allStarted.SetResult();
            }

            await allStarted.Task.WaitAsync(token);
            if (prompt.EndsWith("0", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Simulated independent provider failure.");
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"));
        });
        using var routing = Create(database, decisions, fast, observer);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        var responses = await Task.WhenAll(Enumerable.Range(0, count).Select(index =>
            routing.GetResponseAsync($"Sort [3,1,2] request {index}", cancellationToken: deadline.Token)));
        var records = observer.Snapshot();

        Assert.Equal(count, responses.Select(response => Info(response).RunId).Distinct().Count());
        Assert.Equal(count * 2 + 2, records.Count);
        for (int index = 0; index < count; index++)
        {
            var info = Info(responses[index]);
            var correlated = records.Where(record => record.RunId == info.RunId).ToArray();
            Assert.Equal(index == 0 ? 4 : 2, correlated.Length);
            Assert.Equal(RoutingObservationPhase.RouteSelected, correlated[0].Phase);
            Assert.Equal(RoutingObservationPhase.AttemptFinished, correlated[^1].Phase);
            Assert.Equal(index == 0 ? "balanced" : "fast", correlated[^1].Route);
            Assert.Equal(info.Attempts, correlated.Where(record => record.Attempt is not null).Select(record => record.Attempt));
            Assert.Equal(info.ActualRoute, correlated[^1].Route);
            Assert.Equal(correlated[^1].RouteIdentity, routing.GetRun(info.RunId).ActualRouteIdentity);
            Assert.Equal(TestData.Success, routing.GetRun(info.RunId).Feedback);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObserverFailure_PropagatesWithoutFailoverAndPersistsFailedRun(bool attemptPhase, bool streaming)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var provider = new ScriptedChatClient(FixtureBehavior.Correct);
        RoutingObservation? failed = null;
        var expected = new IOException("observer failure");
        var observer = new CallbackObserver((record, _) =>
        {
            if ((record.Phase == RoutingObservationPhase.AttemptFinished) == attemptPhase)
            {
                failed = record;
                throw expected;
            }

            return ValueTask.CompletedTask;
        });
        using var routing = Create(database, decisions, provider, observer);
        int received = 0;

        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (streaming)
            {
                await foreach (var update in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
                {
                    Assert.Equal("[1,2,3]", update.Text);
                    received++;
                }
            }
            else
            {
                await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
            }
        });

        Assert.Same(expected, error);
        Assert.NotNull(failed);
        Assert.Equal("fast", failed.Route);
        Assert.Equal(attemptPhase ? 1 : 0, provider.Invocations);
        Assert.Equal(streaming && attemptPhase ? 1 : 0, received);
        Assert.Equal(RunStatus.Failed, routing.GetRun(failed.RunId).Status);
        Assert.Null(routing.GetRun(failed.RunId).Feedback);
        Assert.Equal(attemptPhase ? 1 : 0, routing.GetAttempts(failed.RunId).Count);
        Assert.Equal(attemptPhase, failed.Attempt is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionObserverCancellation_StopsInvocationAndRecordsCancelled(bool streaming)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var provider = new ScriptedChatClient(FixtureBehavior.Correct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        RoutingObservation? observed = null;
        var observer = new CallbackObserver((record, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            observed = record;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        using var routing = Create(database, decisions, provider, observer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: cancellation.Token))
                {
                }
            }
            else
            {
                await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: cancellation.Token);
            }
        });

        Assert.NotNull(observed);
        Assert.Equal(0, provider.Invocations);
        Assert.Equal(RunStatus.Cancelled, routing.GetRun(observed.RunId).Status);
        Assert.Empty(routing.GetAttempts(observed.RunId));
        Assert.Null(routing.GetRun(observed.RunId).Feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledProvider_AttemptObserverUsesUncancelledCleanupToken(bool streaming)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        using var provider = new DelegateClient(
            (_, _, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<ChatResponse>(token);
            },
            token => CancelledUpdates(cancellation, token));
        var records = new RecordingRoutingObserver();
        var observer = new CallbackObserver(async (record, token) =>
        {
            if (record.Phase == RoutingObservationPhase.AttemptFinished)
            {
                Assert.Equal(CancellationToken.None, token);
                Assert.True(cancellation.IsCancellationRequested);
                Assert.NotEmpty(new SqliteOutcomeStore(database.Path).GetAttempts(record.RunId));
            }

            await records.ObserveAsync(record, token);
        });
        using var routing = Create(database, decisions, provider, observer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: cancellation.Token))
                {
                }
            }
            else
            {
                await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: cancellation.Token);
            }
        });

        var observed = records.Snapshot();
        Assert.Equal(2, observed.Count);
        Assert.False(observed[1].Attempt!.ResponseCompleted);
        Assert.Equal(streaming, observed[1].Attempt!.OutputCommitted);
        Assert.Equal(RunStatus.Cancelled, routing.GetRun(observed[0].RunId).Status);
        Assert.Null(routing.GetRun(observed[0].RunId).Feedback);
        Assert.Equal(1, provider.Invocations);
    }

    [Fact]
    public async Task ConsoleObserver_WritesDecisionAndActualAttemptBeforeStandardAnswer()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var output = new StringWriter();
        using var provider = new ScriptedChatClient(FixtureBehavior.Correct);
        using var routing = Create(database, decisions, provider, new ConsoleRoutingObserver(output));

        var response = await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
        output.WriteLine(response.Text);
        var info = Info(response);
        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal(
            $"[{info.RunId:N}] decision: recommended=fast probabilities=[fast:0.9, balanced:0.08, strong:0.02] policy=fast route=fast",
            lines[0]);
        Assert.Equal($"[{info.RunId:N}] attempt 1: actual=fast completed=True committed=False", lines[1]);
        Assert.Equal("[1,2,3]", lines[2]);
        Assert.DoesNotContain("Sort", output.ToString());
    }

    [Fact]
    public async Task InjectedHistory_IsUsedAndRemainsCallerOwnedWhileAmbiguousConfigurationFails()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var provider = new ScriptedChatClient(FixtureBehavior.Correct);
        IOutcomeStore history = new SqliteOutcomeStore(database.Path);
        var routes = Routes(provider);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            routes,
            new OutcomeRoutingOptions { History = history });

        var info = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        routing.Dispose();

        Assert.Equal(RunStatus.Completed, history.GetRun(info.RunId).Status);
        Assert.Null(history.GetRun(info.RunId).Feedback);
        Assert.Single(history.GetAttempts(info.RunId));
        Assert.Throws<ArgumentException>(() => new OutcomeRoutingChatClient(
            decisions, routes, new OutcomeRoutingOptions { History = history, HistoryPath = database.Path }));

        var observer = new RecordingRoutingObserver();
        using var broken = new OutcomeRoutingChatClient(
            decisions,
            routes,
            new OutcomeRoutingOptions
            {
                History = new FaultStore(history, nameof(IOutcomeStore.RecordDecision)),
                Observer = observer
            });
        await Assert.ThrowsAsync<IOException>(() =>
            broken.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        Assert.Empty(observer.Snapshot());
        Assert.Equal(1, provider.Invocations);
    }

    [Fact]
    public async Task ObserverFailure_CleansRealRouterPendingStateAndNeverInvokesAlternate()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new ScriptedChatClient(FixtureBehavior.Correct);
        var catalog = new RouteCatalog(Routes(fast));
        var store = new SqliteOutcomeStore(database.Path);
        var observer = new CallbackObserver((_, _) => throw new IOException("selection observer failed"));
        using var router = new OutcomeRouter(decisions, catalog, new PolicySettings(), store, observer);
        var application = new OutcomeApplication(router, catalog, store);
        var task = TestData.Task();

        await Assert.ThrowsAsync<IOException>(() =>
            application.ExecuteAsync(task, TestData.Sort, cancellationToken: TestData.Ct));

        Assert.Equal(0, router.PendingRequestCount);
        Assert.Equal(0, fast.Invocations);
        Assert.Empty(store.GetAttempts(task.RunId));
        Assert.Equal(RunStatus.Failed, store.GetRun(task.RunId).Status);
        Assert.Null(store.GetRun(task.RunId).Feedback);
    }

    private static OutcomeRoutingChatClient Create(
        TestDatabase database,
        IDecisionGenerator decisions,
        IChatClient fast,
        IRoutingObserver? observer = null)
    {
        return new OutcomeRoutingChatClient(
            decisions,
            Routes(fast),
            new OutcomeRoutingOptions
            {
                History = new SqliteOutcomeStore(database.Path),
                Cohort = "sort",
                VerifierFactory = _ => TestData.Sort,
                Observer = observer
            });
    }

    private static ChatRoute[] Routes(IChatClient fast) =>
    [
        TestData.Route("fast", Tier.Fast, fast),
        TestData.Route("balanced", Tier.Balanced),
        TestData.Route("strong", Tier.Strong)
    ];

    private static RoutingResponseInfo Info(ChatResponse response) =>
        Assert.IsType<RoutingResponseInfo>(response.AdditionalProperties![OutcomeRoutingChatClient.ResponseInfoKey]);

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(
        IEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CancelledUpdates(
        CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "[1,");
        cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class CallbackObserver(
        Func<RoutingObservation, CancellationToken, ValueTask> observe) : IRoutingObserver
    {
        public ValueTask ObserveAsync(RoutingObservation observation, CancellationToken cancellationToken) =>
            observe(observation, cancellationToken);
    }
}

[CollectionDefinition("Silent routing client", DisableParallelization = true)]
public sealed class SilentRoutingClientCollection;

[Collection("Silent routing client")]
public sealed class RoutingObservationSilenceTests
{
    [Fact]
    public async Task AbsentObserver_IsSilentOnStandardCalls()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var provider = new ScriptedChatClient(FixtureBehavior.Correct);
        using IChatClient client = new OutcomeRoutingChatClient(
            decisions,
            [
                TestData.Route("fast", Tier.Fast, provider),
                TestData.Route("balanced", Tier.Balanced, provider),
                TestData.Route("strong", Tier.Strong, provider)
            ],
            new OutcomeRoutingOptions { History = new SqliteOutcomeStore(database.Path) });
        using var standardOutput = new StringWriter();
        using var errorOutput = new StringWriter();
        var originalOutput = Console.Out;
        var originalError = Console.Error;

        // This nonparallel collection isolates the process-wide console capture.
        try
        {
            Console.SetOut(standardOutput);
            Console.SetError(errorOutput);
            var response = await client.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
            Assert.Equal("[1,2,3]", response.Text);
            await foreach (var update in client.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
            {
                Assert.Equal("[1,2,3]", update.Text);
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Equal("", standardOutput.ToString());
        Assert.Equal("", errorOutput.ToString());
    }
}

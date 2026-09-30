using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Extensions.AI;

namespace OutcomeRouting.Tests;

public sealed class RoutingAcceptanceTests
{
    [Fact]
    public async Task PreOutputFailover_RecordsEachAttemptAndAttributesFailureToBalancedAlternate()
    {
        using var rig = new TestRig(fast: FixtureBehavior.FailBeforeOutput, balanced: FixtureBehavior.Incorrect);
        var result = await rig.Execute();
        Assert.Equal("balanced", result.ActualRoute);
        Assert.Equal(TestData.Failure, result.Feedback);
        Assert.Equal("[3,1,2]", result.Response.Text);
        Assert.Collection(result.Attempts,
            first => AssertAttempt(first, 1, rig.Catalog.Routes[0], completed: false, committed: false, error: true),
            second => AssertAttempt(second, 2, rig.Catalog.Routes[1], completed: true, committed: false, error: false));
        Assert.Equal(result.Attempts, rig.Store.GetAttempts(result.RunId));
        Assert.Equal(rig.Catalog.Routes[1].Identity, rig.Store.GetRun(result.RunId).ActualRouteIdentity);
        var evidence = Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(new Evidence("balanced", rig.Catalog.Routes[1].Identity, Tier.Balanced,
            Outcome.Failure, Provenance.Verifier), evidence);
        var next = await rig.Execute();
        Assert.Equal(Tier.Fast, next.Decision.Recommended);
        Assert.Equal(Tier.Strong, next.Decision.Selected); // failure at ACTUAL Balanced, not failed Fast
        Assert.Equal("strong", next.ActualRoute);
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(1, rig.Balanced.Invocations);
        Assert.Equal(1, rig.Strong.Invocations);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task ConfigureOptions_ClonesPreservesCallerAndStripsRequestMetadataDownstream()
    {
        using var rig = new TestRig();
        var callerReasoning = new ReasoningOptions { Effort = ReasoningEffort.High };
        var caller = new ChatOptions {
            ModelId = "caller-model", Temperature = .75f, MaxOutputTokens = 777,
            Reasoning = callerReasoning, AdditionalProperties = new() { ["user-tag"] = "keep-me" } };
        var task = new RoutingTask("sort", "Sort [3,1,2]", "Keep duplicates");
        var result = await rig.App.ExecuteAsync(task, TestData.Sort, caller, TestData.Ct);
        Assert.Equal("caller-model", caller.ModelId);
        Assert.Equal(.75f, caller.Temperature);
        Assert.Equal(777, caller.MaxOutputTokens);
        Assert.Same(callerReasoning, caller.Reasoning);
        Assert.Equal(ReasoningEffort.High, caller.Reasoning.Effort);
        Assert.Equal("keep-me", Assert.Single(caller.AdditionalProperties!).Value);
        var observed = rig.Fast.ObservedOptions!;
        Assert.NotSame(caller, observed);
        Assert.NotSame(caller.AdditionalProperties, observed.AdditionalProperties);
        Assert.Equal("fast-model", observed.ModelId);
        Assert.Equal(0f, observed.Temperature);
        Assert.Equal(128, observed.MaxOutputTokens);
        Assert.Null(observed.Reasoning);
        Assert.False(observed.AdditionalProperties!.ContainsKey("outcome-routing-request"));
        Assert.Equal("keep-me", observed.AdditionalProperties["user-tag"]);
        var message = Assert.Single(rig.Fast.ObservedMessages!);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("Sort [3,1,2]\nContext: Keep duplicates", message.Text);
        Assert.Equal(TestData.Success, result.Feedback);
    }

    [Theory]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    public async Task ExplicitRouteSettings_OverrideOnlyDownstreamOptions(string effort, ReasoningEffort expected)
    {
        var client = new ScriptedChatClient(FixtureBehavior.Correct);
        var route = TestData.Route("fast", Tier.Fast, client, model: "specific",
            settings: new(.5, 256, effort));
        var caller = new ChatOptions { ModelId = "untouched", Temperature = 1.5f,
            Reasoning = new() { Effort = ReasoningEffort.Medium }, AdditionalProperties = new() { ["tag"] = 42 } };
        var messages = new[] { new ChatMessage(ChatRole.User, "exact request") };
        var result = await route.Client.GetResponseAsync(messages, caller, TestData.Ct);
        Assert.Equal("[1,2,3]", result.Text);
        Assert.Equal("specific", client.ObservedOptions!.ModelId);
        Assert.Equal(.5f, client.ObservedOptions.Temperature);
        Assert.Equal(256, client.ObservedOptions.MaxOutputTokens);
        Assert.Equal(expected, client.ObservedOptions.Reasoning!.Effort);
        Assert.Equal(42, client.ObservedOptions.AdditionalProperties!["tag"]);
        Assert.Equal("untouched", caller.ModelId);
        Assert.Equal(1.5f, caller.Temperature);
        Assert.Equal(ReasoningEffort.Medium, caller.Reasoning!.Effort);
        Assert.Equal("exact request", Assert.Single(client.ObservedMessages!).Text);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 4096)]
    public async Task RouteSettingEndpoints_AreAcceptedAndApplied(double temperature, int tokens)
    {
        var client = new ScriptedChatClient(FixtureBehavior.Correct);
        var route = TestData.Route("fast", Tier.Fast, client, settings: new(temperature, tokens));
        var response = await route.Client.GetResponseAsync([new(ChatRole.User, "Sort [3,1,2]")],
            cancellationToken: TestData.Ct);
        Assert.Equal("[1,2,3]", response.Text);
        Assert.Equal((float)temperature, client.ObservedOptions!.Temperature);
        Assert.Equal(tokens, client.ObservedOptions.MaxOutputTokens);
        Assert.Equal("fast-model", client.ObservedOptions.ModelId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MaximumAttemptsPerRequest_CapsActualInvocations(int maximum)
    {
        using var rig = new TestRig(FixtureBehavior.FailBeforeOutput, FixtureBehavior.FailBeforeOutput,
            FixtureBehavior.FailBeforeOutput, new() { MaximumAttempts = maximum });
        var task = TestData.Task();
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Execute(task));
        Assert.Equal(maximum, rig.Fast.Invocations + rig.Balanced.Invocations + rig.Strong.Invocations);
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(maximum >= 2 ? 1 : 0, rig.Balanced.Invocations);
        Assert.Equal(maximum >= 3 ? 1 : 0, rig.Strong.Invocations);
        var attempts = rig.Store.GetAttempts(task.RunId);
        Assert.Equal(maximum, attempts.Count);
        Assert.Equal(Enumerable.Range(1, maximum), attempts.Select(a => a.Number));
        Assert.All(attempts, a => { Assert.False(a.ResponseCompleted); Assert.NotNull(a.ErrorType); });
        Assert.Equal(RunStatus.Failed, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task ExhaustedEligibleRoutes_NeverRetriesFailedRoute()
    {
        using var rig = new TestRig(strong: FixtureBehavior.FailBeforeOutput,
            settings: new() { MaximumAttempts = 6 });
        var task = TestData.Task(minimum: Tier.Strong);
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Execute(task));
        Assert.Equal(1, rig.Strong.Invocations);
        Assert.Equal(0, rig.Fast.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations);
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[2],
            completed: false, committed: false, error: true);
        Assert.Equal(RunStatus.Failed, rig.Store.GetRun(task.RunId).Status);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(false, "v1")]
    [InlineData(true, "v1")]
    [InlineData(false, "v2")]
    [InlineData(true, "v2")]
    public async Task PhysicalRouteAliases_AreNotRetriedUnderDifferentTierOrName(bool stream, string aliasRevision)
    {
        using var rig = new TestRig();
        var first = new ScriptedChatClient(FixtureBehavior.FailBeforeOutput);
        var alias = new ScriptedChatClient(FixtureBehavior.Correct);
        var final = new ScriptedChatClient(FixtureBehavior.Correct);
        var catalog = new RouteCatalog([
            TestData.Route("fast", Tier.Fast, first, model: "same-model"),
            TestData.Route("balanced-alias", Tier.Balanced, alias, model: "same-model", revision: aliasRevision),
            TestData.Route("strong", Tier.Strong, final, model: "different-model") ]);
        var router = new OutcomeRouter(new FixtureDecisionGenerator(), catalog, new() { MaximumAttempts = 6 }, rig.Store);
        var app = new OutcomeApplication(router, catalog, rig.Store);
        var task = TestData.Task();
        // Metadata revision changes retain distinct evidence identities but do
        // not make the same endpoint/model/settings a new physical invocation.
        var actual = catalog.Routes[2];
        if (stream)
            Assert.Equal("[1,2,3]", await Consume(app.StreamAsync(task, TestData.Sort, cancellationToken: TestData.Ct)));
        else
        {
            var result = await app.ExecuteAsync(task, TestData.Sort, cancellationToken: TestData.Ct);
            Assert.Equal(actual.Name, result.ActualRoute);
            Assert.Equal(TestData.Success, result.Feedback);
        }
        Assert.Equal(1, first.Invocations);
        Assert.Equal(0, alias.Invocations);
        Assert.Equal(1, final.Invocations);
        Assert.NotEqual(catalog.Routes[0].Identity, catalog.Routes[1].Identity); // evidence remains route-specific
        Assert.Equal(actual.Identity, rig.Store.GetRun(task.RunId).ActualRouteIdentity);
        Assert.Equal(TestData.Success, rig.Store.GetRun(task.RunId).Feedback);
        Assert.Collection(rig.Store.GetAttempts(task.RunId),
            a => AssertAttempt(a, 1, catalog.Routes[0], false, false, true),
            a => AssertAttempt(a, 2, actual, true, stream, false));
        Assert.Equal(actual.Name, Assert.Single(rig.Store.ReadEvidence("sort", catalog.Revision, new())).Route);
        Assert.Equal(0, router.PendingRequestCount);
    }

    [Theory]
    [InlineData("conversation", false)]
    [InlineData("conversation", true)]
    [InlineData("empty-conversation", false)]
    [InlineData("empty-conversation", true)]
    [InlineData("tools", false)]
    [InlineData("tools", true)]
    [InlineData("tool-mode-auto", false)]
    [InlineData("tool-mode-auto", true)]
    [InlineData("tool-mode-none", false)]
    [InlineData("tool-mode-none", true)]
    [InlineData("continuation", false)]
    [InlineData("continuation", true)]
    [InlineData("background", false)]
    [InlineData("background", true)]
    [InlineData("multiple-tools-true", false)]
    [InlineData("multiple-tools-true", true)]
    [InlineData("multiple-tools-false", false)]
    [InlineData("multiple-tools-false", true)]
    [InlineData("undeclared-json", false)]
    [InlineData("undeclared-json", true)]
    [InlineData("undeclared-text-format", false)]
    [InlineData("undeclared-text-format", true)]
    [InlineData("reserved-key", false)]
    [InlineData("reserved-key", true)]
    public async Task UnsupportedCallerOptions_RejectBeforeInvocationAndCleanRun(
        string option, bool stream)
    {
        using var rig = new TestRig();
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        int toolInvocations = 0;
        var caller = new ChatOptions { AdditionalProperties = new() { ["user-tag"] = "unchanged" } };
        switch (option)
        {
            case "conversation": caller.ConversationId = "provider-owned-conversation"; break;
            case "empty-conversation": caller.ConversationId = ""; break;
            case "tools": caller.Tools = [AIFunctionFactory.Create(() => ++toolInvocations, "fixture_tool")]; break;
            case "tool-mode-auto": caller.ToolMode = ChatToolMode.Auto; break;
            case "tool-mode-none": caller.ToolMode = ChatToolMode.None; break;
            case "continuation": caller.ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }); break;
            case "background": caller.AllowBackgroundResponses = true; break;
            case "multiple-tools-true": caller.AllowMultipleToolCalls = true; break;
            case "multiple-tools-false": caller.AllowMultipleToolCalls = false; break;
            case "undeclared-json": caller.ResponseFormat = ChatResponseFormat.Json; break;
            case "undeclared-text-format": caller.ResponseFormat = ChatResponseFormat.Text; break;
            case "reserved-key": caller.AdditionalProperties["outcome-routing-request"] = "caller-supplied"; break;
            default: throw new InvalidOperationException("Unknown test case");
        }
        var error = stream
            ? await Assert.ThrowsAsync<ArgumentException>(() =>
                Consume(rig.App.StreamAsync(task, verifier, caller, TestData.Ct)))
            : await Assert.ThrowsAsync<ArgumentException>(() =>
                rig.App.ExecuteAsync(task, verifier, caller, TestData.Ct));
        Assert.Equal("caller", error.ParamName);
        Assert.Equal(0, rig.Fast.Invocations + rig.Balanced.Invocations + rig.Strong.Invocations);
        Assert.Equal(0, verifier.Invocations);
        Assert.Equal(0, toolInvocations);
        Assert.Equal("unchanged", caller.AdditionalProperties["user-tag"]);
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Null(run.Feedback);
        Assert.Null(run.ActualRouteIdentity);
        Assert.Empty(rig.Store.GetAttempts(task.RunId));
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatelessDeclaredJsonOptions_AllowEmptyToolsAndPreserveCaller(bool stream)
    {
        using var rig = new TestRig();
        var task = new RoutingTask("sort", "Sort [3,1,2]", required: Capability.Text | Capability.Json);
        var caller = new ChatOptions { Tools = [], AllowBackgroundResponses = false,
            ResponseFormat = ChatResponseFormat.Json, AdditionalProperties = new() { ["user-tag"] = "kept" } };
        if (stream)
            Assert.Equal("[1,2,3]", await Consume(rig.App.StreamAsync(task, TestData.Sort, caller, TestData.Ct)));
        else
            Assert.Equal("[1,2,3]", (await rig.App.ExecuteAsync(task, TestData.Sort, caller, TestData.Ct)).Response.Text);
        Assert.Equal(TestData.Success, rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(RunStatus.Completed, rig.Store.GetRun(task.RunId).Status);
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(ChatResponseFormat.Json, rig.Fast.ObservedOptions!.ResponseFormat);
        Assert.False(rig.Fast.ObservedOptions.AllowBackgroundResponses);
        Assert.Empty(rig.Fast.ObservedOptions.Tools!);
        Assert.Empty(caller.Tools);
        Assert.Equal("kept", Assert.Single(caller.AdditionalProperties!).Value);
        Assert.False(rig.Fast.ObservedOptions.AdditionalProperties!.ContainsKey("outcome-routing-request"));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedNullVerifier_ThrowsAndLeavesCompletedRunWithoutFeedback(bool stream)
    {
        using var rig = new TestRig();
        var task = TestData.Task();
        var verifier = new NullVerifier();
        var error = stream
            ? await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct)))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Execute(task, verifier));
        Assert.Equal("Verifier returned null feedback.", error.Message);
        Assert.Equal(1, verifier.Invocations);
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations + rig.Strong.Invocations);
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Completed, run.Status); // provider completed, verifier contract failed
        Assert.Equal(rig.Catalog.Routes[0].Identity, run.ActualRouteIdentity);
        Assert.Null(run.Feedback); // not an explicit immutable Unknown
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], true, stream, false);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
        var later = new Feedback(Outcome.Success, Provenance.Application, "independent-app-check");
        Assert.True(rig.Store.ReportFeedback(task.RunId, run.ActualRouteIdentity!, later));
        Assert.Equal(later, rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(new Evidence("fast", run.ActualRouteIdentity!, Tier.Fast, Outcome.Success, Provenance.Application),
            Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new())));
    }

    [Fact]
    public async Task ClientCancellation_PropagatesAndStopsReselection()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        using var client = new DelegateClient((messages, options, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });
        using var rig = new TestRig(fastClient: client);
        var task = TestData.Task();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.App.ExecuteAsync(task, TestData.Sort, cancellationToken: cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(1, client.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations);
        Assert.Equal(0, rig.Strong.Invocations);
        Assert.Equal(RunStatus.Cancelled, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        Assert.False(Assert.Single(rig.Store.GetAttempts(task.RunId)).ResponseCompleted);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task GeneratorCancellation_PropagatesWithoutCallingAnyRoute()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        using var generator = new DelegateGenerator((input, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });
        using var rig = new TestRig(generator: generator);
        var task = TestData.Task();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.App.ExecuteAsync(task, TestData.Sort, cancellationToken: cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(1, generator.Invocations);
        Assert.Equal(0, rig.Fast.Invocations + rig.Balanced.Invocations + rig.Strong.Invocations);
        Assert.Empty(rig.Store.GetAttempts(task.RunId));
        Assert.Equal(RunStatus.Cancelled, rig.Store.GetRun(task.RunId).Status);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData("ReadEvidence", false)]
    [InlineData("RecordDecision", false)]
    [InlineData("RecordAttempt", false)]
    [InlineData("ReadEvidence", true)]
    [InlineData("RecordDecision", true)]
    [InlineData("RecordAttempt", true)]
    public async Task SelectionAndAttemptHookStorageFaults_ClearPendingEvenWithoutTerminalCallback(string operation, bool stream)
    {
        using var rig = new TestRig(decorate: store => new FaultStore(store, operation));
        var task = TestData.Task();
        if (stream)
            await Assert.ThrowsAsync<IOException>(() =>
                Consume(rig.App.StreamAsync(task, TestData.Sort, cancellationToken: TestData.Ct)));
        else
            await Assert.ThrowsAsync<IOException>(() => rig.Execute(task));
        Assert.Equal(operation == "RecordAttempt" ? 1 : 0, rig.Fast.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations);
        Assert.Empty(rig.Store.GetAttempts(task.RunId));
        Assert.Equal(RunStatus.Failed, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRuns_SharedRouterOrIndependentStores_AreSeparatelyAttributed(bool independent)
    {
        const int count = 4;
        int arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new DelegateClient(async (messages, options, ct) =>
        {
            var text = Assert.Single(messages).Text;
            if (Interlocked.Increment(ref arrived) == count) ready.SetResult();
            await release.Task.WaitAsync(ct);
            return new(new ChatMessage(ChatRole.Assistant, text.Contains("incorrect", StringComparison.Ordinal) ? "[3,1,2]" : "[1,2,3]"));
        });
        using var rig = new TestRig(fastClient: client);
        var tasks = Enumerable.Range(0, count).Select(i =>
            new RoutingTask("sort", $"Sort [3,1,2] run-{i} {(i % 2 == 0 ? "correct" : "incorrect")}")).ToArray();
        var routers = new List<OutcomeRouter>();
        var executions = tasks.Select(task =>
        {
            if (!independent) return rig.App.ExecuteAsync(task, TestData.Sort, cancellationToken: TestData.Ct);
            var store = new SqliteOutcomeStore(rig.Database.Path);
            var router = new OutcomeRouter(new FixtureDecisionGenerator(), rig.Catalog, new(), store);
            routers.Add(router);
            return new OutcomeApplication(router, rig.Catalog, store)
                .ExecuteAsync(task, TestData.Sort, cancellationToken: TestData.Ct);
        }).ToArray();
        await ready.Task.WaitAsync(TestData.Ct);
        Assert.Equal(independent ? 0 : count, rig.Router.PendingRequestCount);
        if (independent) Assert.All(routers, r => Assert.Equal(1, r.PendingRequestCount));
        release.SetResult();
        var results = await Task.WhenAll(executions);
        var observer = new SqliteOutcomeStore(rig.Database.Path);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(tasks[i].RunId, results[i].RunId);
            Assert.Equal(i % 2 == 0 ? TestData.Success : TestData.Failure, results[i].Feedback);
            Assert.Equal(results[i].Feedback, observer.GetRun(tasks[i].RunId).Feedback);
            Assert.Equal(RunStatus.Completed, observer.GetRun(tasks[i].RunId).Status);
            Assert.Equal(rig.Catalog.Routes[0].Identity, observer.GetRun(tasks[i].RunId).ActualRouteIdentity);
            AssertAttempt(Assert.Single(observer.GetAttempts(tasks[i].RunId)), 1, rig.Catalog.Routes[0],
                completed: true, committed: false, error: false);
        }
        Assert.Equal(count, client.Invocations);
        var evidence = observer.ReadEvidence("sort", rig.Catalog.Revision, new());
        Assert.Equal(2, evidence.Count(e => e.Outcome == Outcome.Success));
        Assert.Equal(2, evidence.Count(e => e.Outcome == Outcome.Failure));
        Assert.Equal(0, rig.Router.PendingRequestCount);
        Assert.All(routers, r => Assert.Equal(0, r.PendingRequestCount));
    }

    [Fact]
    public async Task StreamingPreOutputFailure_FailsOverAndVerifiesCompletedBalancedText()
    {
        using var rig = new TestRig(fast: FixtureBehavior.FailBeforeOutput);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        var text = await Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct));
        Assert.Equal("[1,2,3]", text);
        Assert.Equal(1, verifier.Invocations);
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(TestData.Success, run.Feedback);
        Assert.Equal(rig.Catalog.Routes[1].Identity, run.ActualRouteIdentity);
        Assert.Collection(rig.Store.GetAttempts(task.RunId),
            first => AssertAttempt(first, 1, rig.Catalog.Routes[0], false, false, true),
            second => AssertAttempt(second, 2, rig.Catalog.Routes[1], true, true, false));
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(1, rig.Balanced.Invocations);
        Assert.Equal(0, rig.Strong.Invocations);
        Assert.Equal("balanced", Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new())).Route);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task StreamingPostFirstUpdateFailure_NeverInvokesAlternateOrVerifier()
    {
        using var rig = new TestRig(fast: FixtureBehavior.FailAfterOutput);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        await using var iterator = rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct).GetAsyncEnumerator(TestData.Ct);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("[1,2,3]", iterator.Current.Text);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await iterator.MoveNextAsync());
        Assert.Equal(0, verifier.Invocations);
        Assert.Equal(1, rig.Fast.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations + rig.Strong.Invocations);
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], false, true, true);
        Assert.Equal(RunStatus.Failed, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(FixtureBehavior.Correct, Outcome.Success)]
    [InlineData(FixtureBehavior.Incorrect, Outcome.Failure)]
    public async Task CompletedSupportedTextStream_RunsIndependentVerifier(FixtureBehavior behavior, Outcome outcome)
    {
        using var rig = new TestRig(fast: behavior);
        var verifier = new CountingVerifier(TestData.Sort);
        var task = TestData.Task();
        Assert.Equal(behavior == FixtureBehavior.Correct ? "[1,2,3]" : "[3,1,2]",
            await Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct)));
        Assert.Equal(1, verifier.Invocations);
        Assert.Equal(outcome, rig.Store.GetRun(task.RunId).Feedback!.Outcome);
        Assert.Equal(Provenance.Verifier, rig.Store.GetRun(task.RunId).Feedback!.Provenance);
        Assert.Equal(RunStatus.Completed, rig.Store.GetRun(task.RunId).Status);
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], true, true, false);
        Assert.Equal(outcome, Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new())).Outcome);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task EmptyStream_IsExplicitImmutableUnknownNotSuccess()
    {
        using var rig = new TestRig(fast: FixtureBehavior.Empty);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        Assert.Equal("", await Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct)));
        Assert.Equal(0, verifier.Invocations);
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(Feedback.Unknown("empty-stream"), run.Feedback);
        Assert.Throws<InvalidOperationException>(() =>
            rig.Store.ReportFeedback(task.RunId, run.ActualRouteIdentity!, TestData.Success));
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], true, false, false);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task UnsupportedStreamContent_IsUnknownWithoutCallingTextVerifier()
    {
        using var client = new DelegateClient((m, o, ct) => throw new InvalidOperationException("stream only"),
            UnsupportedStream);
        using var rig = new TestRig(fastClient: client);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        Assert.Equal("[1,2,3]", await Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct)));
        Assert.Equal(0, verifier.Invocations);
        Assert.Equal(Feedback.Unknown("unsupported-stream"), rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(RunStatus.Completed, rig.Store.GetRun(task.RunId).Status);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData("assistant", true)]
    [InlineData("null", true)]
    [InlineData("user", false)]
    [InlineData("system", false)]
    [InlineData("tool", false)]
    [InlineData("user-first", false)]
    [InlineData("user-last", false)]
    public async Task CompletedTextStream_VerifiesOnlyAssistantOrNullRoles(string role, bool supported)
    {
        using var client = new DelegateClient((m, o, ct) => throw new InvalidOperationException("stream only"),
            ct => TextRoleStream(role, ct));
        using var rig = new TestRig(fastClient: client);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        Assert.Equal("[1,2,3]", await Consume(rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct)));
        Assert.Equal(supported ? 1 : 0, verifier.Invocations);
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(supported ? TestData.Success : Feedback.Unknown("unsupported-stream"), run.Feedback);
        Assert.Equal(rig.Catalog.Routes[0].Identity, run.ActualRouteIdentity);
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], true, true, false);
        var evidence = rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new());
        if (supported) Assert.Equal(Outcome.Success, Assert.Single(evidence).Outcome);
        else Assert.Empty(evidence);
        Assert.Equal(1, client.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations + rig.Strong.Invocations);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task StreamingEarlyDispose_IsAbandonedAndCleansPendingWithoutPositiveFeedback()
    {
        using var rig = new TestRig();
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        var stream = rig.App.StreamAsync(task, verifier, cancellationToken: TestData.Ct);
        Assert.Throws<KeyNotFoundException>(() => rig.Store.GetRun(task.RunId)); // durable run starts on enumeration
        var iterator = stream.GetAsyncEnumerator(TestData.Ct);
        try
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("[1,2,3]", iterator.Current.Text);
            Assert.Equal(1, rig.Router.PendingRequestCount);
        }
        finally { await iterator.DisposeAsync(); }
        var run = rig.Store.GetRun(task.RunId);
        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.Null(run.Feedback);
        Assert.Null(run.ActualRouteIdentity);
        Assert.Equal(0, verifier.Invocations);
        AssertAttempt(Assert.Single(rig.Store.GetAttempts(task.RunId)), 1, rig.Catalog.Routes[0], false, true, false);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task StreamingCancellationAfterOutput_StopsReselectionAndPropagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        using var client = new DelegateClient((m, o, ct) => throw new InvalidOperationException("stream only"), ct => CancelAfterFirst(ct, cts));
        using var rig = new TestRig(fastClient: client);
        var task = TestData.Task();
        var verifier = new CountingVerifier(TestData.Sort);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Consume(rig.App.StreamAsync(task, verifier, cancellationToken: cts.Token)));
        Assert.True(error.CancellationToken.IsCancellationRequested); // async iterator may link enumerator tokens
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(1, client.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations + rig.Strong.Invocations);
        Assert.Equal(0, verifier.Invocations);
        Assert.Equal(RunStatus.Cancelled, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    private static async Task<string> Consume(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in stream.WithCancellation(TestData.Ct)) text.Append(update.Text);
        return text.ToString();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> UnsupportedStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var update = new ChatResponseUpdate(ChatRole.Assistant, "[1,2,3]");
        update.Contents.Add(new DataContent(new byte[] { 1 }, "image/png"));
        yield return update;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextRoleStream(string role,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        if (role is "user-first" or "user-last")
        {
            yield return new(role == "user-first" ? ChatRole.User : ChatRole.Assistant, "[1,");
            yield return new(role == "user-last" ? ChatRole.User : ChatRole.Assistant, "2,3]");
            yield break;
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, "[1,2,3]")
        { Role = role == "null" ? null : new ChatRole(role) };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CancelAfterFirst(
        [EnumeratorCancellation] CancellationToken ct, CancellationTokenSource cts)
    {
        await Task.CompletedTask;
        yield return new(ChatRole.Assistant, "[1,2,3]");
        cts.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private static void AssertAttempt(AttemptRecord attempt, int ordinal, ChatRoute route,
        bool completed, bool committed, bool error)
    {
        Assert.Equal(ordinal, attempt.Number);
        Assert.Equal(route.Name, attempt.Route);
        Assert.Equal(route.Identity, attempt.RouteIdentity);
        Assert.Equal(route.Tier, attempt.Tier);
        Assert.Equal(completed, attempt.ResponseCompleted);
        Assert.Equal(committed, attempt.OutputCommitted);
        if (error) Assert.Equal(typeof(HttpRequestException).FullName, attempt.ErrorType);
        else Assert.Null(attempt.ErrorType);
        if (committed) Assert.NotNull(attempt.FirstUpdateTicks);
        else Assert.Null(attempt.FirstUpdateTicks);
    }
}

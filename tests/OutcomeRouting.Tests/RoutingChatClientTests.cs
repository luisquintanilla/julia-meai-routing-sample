using System.Collections;
using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace OutcomeRouting.Tests;

public sealed class RoutingChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_PersistsVerifiedFailureAndRaisesNextRequestAcrossReload()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model", FixtureBehavior.Incorrect);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");

        using (var routing = Create(decisions, fast, balanced, strong, database.Path))
        {
            var first = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

            Assert.Equal("fast", first.ActualRoute);
            Assert.Equal(Outcome.Failure, first.Feedback.Outcome);
            Assert.Equal(Provenance.Verifier, first.Feedback.Provenance);
            Assert.Single(first.Attempts);
            Assert.Equal(RunStatus.Completed, routing.GetRun(first.RunId).Status);
        }

        using var reopened = Create(decisions, fast, balanced, strong, database.Path);
        var next = Info(await reopened.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(Tier.Fast, next.RecommendedTier);
        Assert.Equal(Tier.Balanced, next.SelectedTier);
        Assert.Equal("balanced", next.ActualRoute);
        Assert.Equal(Outcome.Success, next.Feedback.Outcome);
    }

    [Fact]
    public async Task OmittedVerifier_RemainsUnknownUntilApplicationReportsActualCompletedOutcome()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path, verify: false);

        var first = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        var nextUnknown = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(Outcome.Unknown, first.Feedback.Outcome);
        Assert.Null(routing.GetRun(first.RunId).Feedback);
        Assert.Equal(Tier.Fast, nextUnknown.SelectedTier);

        var failure = new Feedback(Outcome.Failure, Provenance.Application, "application-check-v1");
        Assert.True(routing.ReportFeedback(first.RunId, failure));
        Assert.False(routing.ReportFeedback(first.RunId, failure));
        Assert.Throws<InvalidOperationException>(() => routing.ReportFeedback(first.RunId, TestData.Success));

        var afterFeedback = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        Assert.Equal("balanced", afterFeedback.ActualRoute);
        Assert.Equal(failure, routing.GetRun(first.RunId).Feedback);
    }

    [Fact]
    public async Task PreOutputFailure_AttributesVerificationToAlternateAndClonesOptions()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model", FixtureBehavior.FailBeforeOutput);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        var callerOptions = new ChatOptions { Temperature = 0.7f, ModelId = "caller-model" };

        var result = Info(await routing.GetResponseAsync(
            "Sort [3,1,2]",
            callerOptions,
            TestData.Ct));

        Assert.Equal("balanced", result.ActualRoute);
        Assert.Equal(Outcome.Success, result.Feedback.Outcome);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(result.Attempts[1].RouteIdentity, routing.GetRun(result.RunId).ActualRouteIdentity);
        Assert.Equal("caller-model", callerOptions.ModelId);
        Assert.Equal(0.7f, callerOptions.Temperature);
        Assert.Null(callerOptions.AdditionalProperties);
        Assert.Equal("balanced-model", balanced.Inner.ObservedOptions!.ModelId);
        Assert.False(balanced.Inner.ObservedOptions.AdditionalProperties!.ContainsKey("outcome-routing-request"));
    }

    [Fact]
    public async Task StreamingEarlyDispose_PersistsAbandonedRatherThanSuccess()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var client = new MetadataClient("shared-model");
        using var routing = Create(decisions, client, client, client, database.Path);
        await using (var iterator = routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct)
            .GetAsyncEnumerator(TestData.Ct))
        {
            Assert.True(await iterator.MoveNextAsync());
        }

        var runId = OnlyRunId(database);
        Assert.Equal(RunStatus.Abandoned, routing.GetRun(runId).Status);
        Assert.Null(routing.GetRun(runId).Feedback);
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndBorrowsSharedClientsAndGenerator()
    {
        using var database = new TestDatabase();
        using var decisions = new OwnedGenerator();
        using var client = new MetadataClient("shared-model");
        var routing = Create(decisions, client, client, client, database.Path);

        await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
        routing.Dispose();
        routing.Dispose();

        Assert.False(decisions.Disposed);
        Assert.Equal(0, client.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        Assert.Throws<ObjectDisposedException>(() => routing.GetRun(Guid.NewGuid()));
        Assert.Throws<ObjectDisposedException>(() => routing.ReportFeedback(Guid.NewGuid(), TestData.Success));

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
        });
        Assert.Throws<ObjectDisposedException>(() => routing.GetService(typeof(IChatClient)));
    }

    [Fact]
    public void Create_InvalidPolicyDoesNotCreateStoreOrDisposeBorrowedResources()
    {
        using var database = new TestDatabase();
        using var decisions = new OwnedGenerator();
        using var client = new MetadataClient("shared-model");

        Assert.Throws<ArgumentException>(() => new OutcomeRoutingChatClient(
            decisions,
            [
                ChatRoute.Create(Tier.Fast, client),
                ChatRoute.Create(Tier.Balanced, client),
                ChatRoute.Create(Tier.Strong, client)
            ],
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                Policy = new PolicySettings { MaximumAttempts = 0 }
            }));

        Assert.False(File.Exists(database.Path));
        Assert.False(decisions.Disposed);
        Assert.Equal(0, client.DisposeCount);
    }

    [Fact]
    public void RouteFactory_UsesMetadataAndKeepsConfigIsolation()
    {
        using var client = new MetadataClient("concrete-model");
        var inferred = ChatRoute.Create(Tier.Fast, client);
        var explicitRoute = new ChatRoute(
            "fast", Tier.Fast, Capability.Text, new Uri("fixture://local").AbsoluteUri, "concrete-model", "v1", client);
        var revised = ChatRoute.Create(Tier.Fast, client, configRevision: "v2");

        Assert.Equal("concrete-model", inferred.Model);
        Assert.Equal(explicitRoute.Identity, inferred.Identity);
        Assert.NotEqual(inferred.Identity, revised.Identity);
    }

    [Theory]
    [InlineData(null, "model")]
    [InlineData("fixture://local", null)]
    [InlineData("fixture://local?secret=value", "model")]
    [InlineData("https://user:secret@example.invalid", "model")]
    public void RouteFactory_RejectsUnknownOrSensitiveIdentity(string? endpoint, string? model)
    {
        using var client = new MetadataClient(model, endpoint: endpoint);
        Assert.Throws<ArgumentException>(() => ChatRoute.Create(Tier.Fast, client));
    }

    [Fact]
    public async Task FullHistory_SnapshotsOncePreservesRolesFragmentsWhitespaceAndProviderResponse()
    {
        using var database = new TestDatabase();
        using var fixture = new FixtureDecisionGenerator();
        using var decisions = new DelegateGenerator((input, token) => fixture.GenerateAsync(input, cancellationToken: token));
        var usage = new UsageDetails { InputTokenCount = 17, OutputTokenCount = 4, TotalTokenCount = 21 };
        var continuation = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 });
        var raw = new object();
        var providerMetadata = new object();
        var providerResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"))
        {
            ResponseId = "provider-response",
            ModelId = "concrete-provider-model",
            ConversationId = "preserved-but-not-reusable-here",
            ContinuationToken = continuation,
            FinishReason = ChatFinishReason.Stop,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Usage = usage,
            RawRepresentation = raw,
            AdditionalProperties = new() { ["provider.metadata"] = providerMetadata }
        };
        using var fast = new ContractClient(providerResponse);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var client = Create(decisions, fast, balanced, strong, database.Path, verify: false);

        ChatMessage[] history =
        [
            new(ChatRole.System, "  Return JSON.\n") { AuthorName = "application" },
            new(ChatRole.User, [new TextContent("Sort "), new TextContent("[3,1,2].  ")]),
            new(ChatRole.Assistant, " Previous answer. "),
            new(ChatRole.User, "\nTry again.\t")
        ];
        var messages = new SingleUseMessages(history);

        ChatResponse response = await client.GetResponseAsync(messages, cancellationToken: TestData.Ct);

        Assert.Equal(1, messages.Enumerations);
        Assert.Equal(history.Length, fast.Messages!.Count);
        for (int index = 0; index < history.Length; index++)
        {
            Assert.Same(history[index], fast.Messages[index]);
        }

        Assert.Equal("Sort [3,1,2].  ", fast.Messages[1].Text);
        Assert.Equal(2, fast.Messages[1].Contents.Count);
        Assert.Equal("application", fast.Messages[0].AuthorName);
        Assert.Equal(
            "system:   Return JSON.\n\nuser: Sort [3,1,2].  \nassistant:  Previous answer. \nuser: \nTry again.\t",
            decisions.Input!.State.GetProperty("Task").GetString());
        Assert.Same(providerResponse, response);
        Assert.Same(providerResponse.Messages[0], response.Messages[0]);
        Assert.Equal("[1,2,3]", response.Text);
        Assert.Equal("provider-response", response.ResponseId);
        Assert.Equal("concrete-provider-model", response.ModelId);
        Assert.Equal("preserved-but-not-reusable-here", response.ConversationId);
        Assert.Same(continuation, response.ContinuationToken);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(DateTimeOffset.UnixEpoch, response.CreatedAt);
        Assert.Same(usage, response.Usage);
        Assert.NotNull(response.Usage);
        Assert.Equal(17, response.Usage.InputTokenCount);
        Assert.Equal(4, response.Usage.OutputTokenCount);
        Assert.Same(raw, response.RawRepresentation);
        Assert.Same(providerMetadata, response.AdditionalProperties!["provider.metadata"]);
        Assert.Equal(Outcome.Unknown, Info(response).Feedback.Outcome);
    }

    [Fact]
    public async Task PromptExtension_ForwardsTheUserPromptWithoutRebuildingIt()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using IChatClient client = Create(decisions, fast, balanced, strong, database.Path);

        await client.GetResponseAsync("  Sort [3,1,2]\n", cancellationToken: TestData.Ct);

        var message = Assert.Single(fast.Inner.ObservedMessages!);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("  Sort [3,1,2]\n", message.Text);
        Assert.IsType<TextContent>(Assert.Single(message.Contents));
    }

    [Fact]
    public async Task BuilderConfigureOptions_ClonesCallerOptionsAndForwardsHonestServices()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        using IChatClient client = routing.AsBuilder()
            .ConfigureOptions(options => options.TopP = 0.25f)
            .Build();
        var marker = new object();
        var callerOptions = new ChatOptions
        {
            TopP = 0.75f,
            Temperature = 0.7f,
            ModelId = "caller-model",
            AdditionalProperties = new() { ["application.marker"] = marker }
        };

        ChatResponse response = await client.GetResponseAsync(
            "Sort [3,1,2]",
            callerOptions,
            TestData.Ct);

        Assert.Equal(0.75f, callerOptions.TopP);
        Assert.Equal(0.7f, callerOptions.Temperature);
        Assert.Equal("caller-model", callerOptions.ModelId);
        Assert.Single(callerOptions.AdditionalProperties);
        Assert.Same(marker, callerOptions.AdditionalProperties["application.marker"]);
        Assert.NotSame(callerOptions, fast.Inner.ObservedOptions);
        Assert.NotSame(callerOptions.AdditionalProperties, fast.Inner.ObservedOptions!.AdditionalProperties);
        Assert.Equal(0.25f, fast.Inner.ObservedOptions.TopP);
        Assert.Equal(0, fast.Inner.ObservedOptions.Temperature);
        Assert.Equal("fast-model", fast.Inner.ObservedOptions.ModelId);
        Assert.Same(marker, fast.Inner.ObservedOptions.AdditionalProperties!["application.marker"]);
        Assert.False(fast.Inner.ObservedOptions.AdditionalProperties.ContainsKey("outcome-routing-request"));
        Assert.Same(routing, client.GetService<OutcomeRoutingChatClient>());
        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService<ChatClientMetadata>());
        Assert.Equal("outcome-routing", metadata.ProviderName);
        Assert.Null(metadata.ProviderUri);
        Assert.Null(metadata.DefaultModelId);
        Assert.Null(client.GetService<ScriptedChatClient>());
        Assert.Null(routing.GetService(typeof(OutcomeRoutingChatClient), "unknown-key"));
        Assert.Throws<ArgumentNullException>(() => routing.GetService(null!));
        Assert.Equal(Outcome.Success, Info(response).Feedback.Outcome);

        client.Dispose();
        Assert.Equal(0, fast.DisposeCount + balanced.DisposeCount + strong.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => routing.GetRun(Info(response).RunId));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null-message")]
    [InlineData("tool-role")]
    [InlineData("custom-role")]
    [InlineData("image")]
    [InlineData("empty-content")]
    public async Task UnsupportedMessages_RejectBeforeInferencePersistenceOrProvider(string input)
    {
        using var database = new TestDatabase();
        using var fixture = new FixtureDecisionGenerator();
        using var decisions = new DelegateGenerator((request, token) => fixture.GenerateAsync(request, cancellationToken: token));
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        ChatMessage[] messages = input switch
        {
            "empty" => [],
            "null-message" => [null!],
            "tool-role" => [new(ChatRole.Tool, "tool output")],
            "custom-role" => [new(new ChatRole("custom"), "custom output")],
            "image" => [new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])],
            "empty-content" => [new(ChatRole.User, [])],
            _ => throw new InvalidOperationException()
        };

        if (input == "empty")
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                routing.GetResponseAsync(messages, cancellationToken: TestData.Ct));
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                routing.GetResponseAsync(messages, cancellationToken: TestData.Ct));
        }

        Assert.Equal(0, decisions.Invocations);
        Assert.Equal(0, fast.Inner.Invocations + balanced.Inner.Invocations + strong.Inner.Invocations);
        Assert.Equal(0, RunCount(database));
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("continuation")]
    [InlineData("background")]
    [InlineData("tools")]
    [InlineData("tool-mode")]
    [InlineData("parallel-tools")]
    [InlineData("undeclared-json")]
    [InlineData("reserved-key")]
    public async Task UnsupportedOptions_RejectBeforeInferencePersistenceOrProvider(string setting)
    {
        using var database = new TestDatabase();
        using var fixture = new FixtureDecisionGenerator();
        using var decisions = new DelegateGenerator((request, token) => fixture.GenerateAsync(request, cancellationToken: token));
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        var options = setting switch
        {
            "conversation" => new ChatOptions { ConversationId = "provider-owned" },
            "continuation" => new ChatOptions { ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1 }) },
            "background" => new ChatOptions { AllowBackgroundResponses = true },
            "tools" => new ChatOptions { Tools = [AIFunctionFactory.Create(() => "not-invoked")] },
            "tool-mode" => new ChatOptions { ToolMode = ChatToolMode.Auto },
            "parallel-tools" => new ChatOptions { AllowMultipleToolCalls = false },
            "undeclared-json" => new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            "reserved-key" => new ChatOptions { AdditionalProperties = new() { ["outcome-routing-request"] = new object() } },
            _ => throw new InvalidOperationException()
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", options, TestData.Ct));

        Assert.Equal(0, decisions.Invocations);
        Assert.Equal(0, fast.Inner.Invocations + balanced.Inner.Invocations + strong.Inner.Invocations);
        Assert.Equal(0, RunCount(database));
    }

    [Fact]
    public async Task ProjectionBudget_RejectsFullHistoryRatherThanDroppingEarlierMessages()
    {
        using var database = new TestDatabase();
        using var fixture = new FixtureDecisionGenerator();
        using var decisions = new DelegateGenerator((request, token) => fixture.GenerateAsync(request, cancellationToken: token));
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path, verify: false);

        await routing.GetResponseAsync(new string('a', 300), cancellationToken: TestData.Ct);
        Assert.Equal(new string('a', 300), fast.Inner.ObservedMessages![0].Text);
        Assert.Equal(1, decisions.Invocations);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            routing.GetResponseAsync(new string('a', 301), cancellationToken: TestData.Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => routing.GetResponseAsync(
            [new(ChatRole.System, new string('a', 290)), new(ChatRole.User, "short request")],
            cancellationToken: TestData.Ct));

        Assert.Equal(1, decisions.Invocations);
        Assert.Equal(1, fast.Inner.Invocations);
        Assert.Equal(1, RunCount(database));
    }

    [Fact]
    public async Task RequestSpecificVerifier_DoesNotGradeUnrelatedTasksAndMetadataIsImmutable()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        var verifier = new CountingVerifier(TestData.Sort);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(fast, balanced, strong),
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                Cohort = "sort",
                VerifierFactory = messages => messages.Count == 1 && messages[0].Text == "Sort [3,1,2]"
                    ? verifier
                    : null
            });

        var unrelated = Info(await routing.GetResponseAsync("Write a poem.", cancellationToken: TestData.Ct));
        Assert.Equal(Outcome.Unknown, unrelated.Feedback.Outcome);
        Assert.Null(routing.GetRun(unrelated.RunId).Feedback);
        Assert.Equal(0, verifier.Invocations);

        var known = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));
        Assert.Equal(Outcome.Success, known.Feedback.Outcome);
        Assert.Equal(1, verifier.Invocations);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, double>)known.Probabilities).Add("other", 1));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)known.Reasons).Add("other"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AttemptRecord>)known.Attempts).Clear());
        Assert.DoesNotContain(typeof(RoutingResponseInfo).GetProperties(), property =>
            property.Name is "Task" or "Messages" or "ProjectedState");
    }

    [Fact]
    public async Task AliasedClients_AreNotRetriedAsDifferentPhysicalRoutes()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var shared = new MetadataClient("same-model", FixtureBehavior.FailBeforeOutput);
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, shared, shared, strong, database.Path);

        var result = Info(await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(1, shared.Inner.Invocations);
        Assert.Equal(1, strong.Inner.Invocations);
        Assert.Equal("strong", result.ActualRoute);
        Assert.Equal(new[] { "fast", "strong" }, result.Attempts.Select(attempt => attempt.Route));
        Assert.Equal(Outcome.Success, result.Feedback.Outcome);
    }

    [Fact]
    public async Task ConcurrentCalls_KeepRunsMessagesFeedbackAndActualRoutesSeparate()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        const int count = 8;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
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
                throw new HttpRequestException("independent simulated provider failure");
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"))
            {
                AdditionalProperties = new() { ["request"] = prompt }
            };
        });
        using var balanced = new DelegateClient((messages, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"))
            {
                AdditionalProperties = new() { ["request"] = messages.Single().Text }
            }));
        using var strong = new ScriptedChatClient(FixtureBehavior.Correct);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            [
                TestData.Route("fast", Tier.Fast, fast),
                TestData.Route("balanced", Tier.Balanced, balanced),
                TestData.Route("strong", Tier.Strong, strong)
            ],
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                Cohort = "sort",
                VerifierFactory = _ => TestData.Sort
            });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        var responses = await Task.WhenAll(Enumerable.Range(0, count)
            .Select(index => routing.GetResponseAsync($"Sort [3,1,2] request {index}", cancellationToken: deadline.Token)));

        Assert.Equal(count, responses.Select(response => Info(response).RunId).Distinct().Count());
        for (int index = 0; index < count; index++)
        {
            var response = responses[index];
            var result = Info(response);
            var run = routing.GetRun(result.RunId);
            Assert.Equal($"Sort [3,1,2] request {index}", response.AdditionalProperties!["request"]);
            Assert.Equal(index == 0 ? "balanced" : "fast", result.ActualRoute);
            Assert.Equal(index == 0 ? 2 : 1, result.Attempts.Count);
            Assert.Equal(result.Attempts[^1].RouteIdentity, run.ActualRouteIdentity);
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal(TestData.Success, run.Feedback);
        }
    }

    [Fact]
    public async Task Streaming_SnapshotsAtCallTimeAndForwardsExactUpdatesWithoutTelemetryUpdates()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var first = new ChatResponseUpdate(ChatRole.Assistant, "[1,")
        {
            ModelId = "provider-model",
            AdditionalProperties = new() { ["chunk"] = 1 }
        };
        var last = new ChatResponseUpdate(ChatRole.Assistant, "2,3]")
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new() { ["chunk"] = 2 }
        };
        using var fast = new ContractClient(updates: [first, last]);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        ChatMessage[] original =
        [
            new(ChatRole.System, " Return JSON.\n"),
            new(ChatRole.User, [new TextContent("Sort "), new TextContent("[3,1,2]")])
        ];
        var mutableSequence = new List<ChatMessage>(original);
        var messages = new SingleUseMessages(mutableSequence);
        var options = new ChatOptions { TopP = 0.25f };

        var stream = routing.GetStreamingResponseAsync(messages, options, TestData.Ct);
        options.TopP = 0.75f;
        mutableSequence.Clear();
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in stream)
        {
            updates.Add(update);
        }

        Assert.Equal(1, messages.Enumerations);
        Assert.Equal(2, updates.Count);
        Assert.Same(first, updates[0]);
        Assert.Same(last, updates[1]);
        Assert.Equal("provider-model", updates[0].ModelId);
        Assert.Equal(ChatFinishReason.Stop, updates[1].FinishReason);
        Assert.Equal(1, updates[0].AdditionalProperties!["chunk"]);
        Assert.Equal(2, updates[1].AdditionalProperties!["chunk"]);
        Assert.Equal(original.Length, fast.Messages!.Count);
        Assert.Same(original[0], fast.Messages[0]);
        Assert.Same(original[1], fast.Messages[1]);
        Assert.Equal(" Return JSON.\n", fast.Messages[0].Text);
        Assert.Equal(2, fast.Messages[1].Contents.Count);
        Assert.Equal(0.25f, fast.Options!.TopP);
        Assert.Equal(0.75f, options.TopP);
        var run = routing.GetRun(OnlyRunId(database));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(TestData.Success, run.Feedback);
    }

    [Fact]
    public async Task StreamingPreOutputFailure_VerifiesOnlyTheActualAlternate()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model", FixtureBehavior.FailBeforeOutput);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        List<ChatResponseUpdate> updates = [];

        await foreach (var update in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
        {
            updates.Add(update);
        }

        Assert.Equal("[1,2,3]", Assert.Single(updates).Text);
        var runId = OnlyRunId(database);
        var attempts = routing.GetAttempts(runId);
        Assert.Equal(new[] { "fast", "balanced" }, attempts.Select(attempt => attempt.Route));
        Assert.False(attempts[0].ResponseCompleted);
        Assert.True(attempts[1].ResponseCompleted);
        Assert.Equal(attempts[1].RouteIdentity, routing.GetRun(runId).ActualRouteIdentity);
        Assert.Equal(TestData.Success, routing.GetRun(runId).Feedback);
        Assert.Equal(0, strong.Inner.Invocations);
    }

    [Fact]
    public async Task StreamingFailureAfterOutput_NeverRetriesOrVerifiesPartialOutput()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new ContractClient(failAfterOutput: true);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        var verifier = new CountingVerifier(TestData.Sort);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(fast, balanced, strong),
            new OutcomeRoutingOptions { HistoryPath = database.Path, VerifierFactory = _ => verifier });

        await using var iterator = routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct)
            .GetAsyncEnumerator(TestData.Ct);
        Assert.True(await iterator.MoveNextAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => iterator.MoveNextAsync().AsTask());

        var runId = OnlyRunId(database);
        Assert.Equal(RunStatus.Failed, routing.GetRun(runId).Status);
        Assert.Null(routing.GetRun(runId).Feedback);
        Assert.True(Assert.Single(routing.GetAttempts(runId)).OutputCommitted);
        Assert.Equal(0, balanced.Inner.Invocations + strong.Inner.Invocations);
        Assert.Equal(0, verifier.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingCancellation_StopsReselectionAndDoesNotRecordQuality(bool afterOutput)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new ContractClient();
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        var stream = routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
        await using var iterator = stream.GetAsyncEnumerator(cancellation.Token);
        if (afterOutput)
        {
            Assert.True(await iterator.MoveNextAsync());
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.Equal(0, balanced.Inner.Invocations + strong.Inner.Invocations);
        if (afterOutput)
        {
            var runId = OnlyRunId(database);
            Assert.Equal(RunStatus.Cancelled, routing.GetRun(runId).Status);
            Assert.Null(routing.GetRun(runId).Feedback);
            Assert.True(Assert.Single(routing.GetAttempts(runId)).OutputCommitted);
        }
        else
        {
            Assert.Equal(0, fast.Invocations);
            Assert.Equal(0, RunCount(database));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenVerifier_PropagatesAndLeavesCompletedRunOpenForApplicationFeedback(bool stream)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(fast, balanced, strong),
            new OutcomeRoutingOptions { HistoryPath = database.Path, VerifierFactory = _ => new NullVerifier() });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (stream)
            {
                await foreach (var _ in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
                {
                }
            }
            else
            {
                await routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct);
            }
        });

        var runId = OnlyRunId(database);
        Assert.Equal(RunStatus.Completed, routing.GetRun(runId).Status);
        Assert.Null(routing.GetRun(runId).Feedback);
        Assert.True(routing.ReportFeedback(runId, new Feedback(Outcome.Success, Provenance.Application, "checked")));
        Assert.Equal(0, balanced.Inner.Invocations + strong.Inner.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionAndHookStorageErrors_PropagateWithoutSuccessShapedFallback(bool hook)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new DelegateClient((_, _, _) =>
        {
            if (hook)
            {
                database.Sql("DROP TABLE attempts;");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]")));
        });
        using var balanced = new ScriptedChatClient(FixtureBehavior.Correct);
        using var strong = new ScriptedChatClient(FixtureBehavior.Correct);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            [
                TestData.Route("fast", Tier.Fast, fast),
                TestData.Route("balanced", Tier.Balanced, balanced),
                TestData.Route("strong", Tier.Strong, strong)
            ],
            new OutcomeRoutingOptions { HistoryPath = database.Path });
        if (!hook)
        {
            database.Sql("DROP TABLE feedback;");
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(hook ? 1 : 0, fast.Invocations);
        Assert.Equal(0, balanced.Invocations + strong.Invocations);
        using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM runs;";
        Assert.Equal("Failed", command.ExecuteScalar());
    }

    [Fact]
    public async Task ReservedResponseMetadataCollision_FailsExplicitlyWithoutOverwritingProviderMetadata()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]"))
        {
            AdditionalProperties = new() { [OutcomeRoutingChatClient.ResponseInfoKey] = "provider-owned" }
        };
        using var fast = new ContractClient(response);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path, verify: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal("provider-owned", response.AdditionalProperties[OutcomeRoutingChatClient.ResponseInfoKey]);
        Assert.Equal(RunStatus.Completed, routing.GetRun(OnlyRunId(database)).Status);
        Assert.Null(routing.GetRun(OnlyRunId(database)).Feedback);
        Assert.Equal(0, balanced.Inner.Invocations + strong.Inner.Invocations);
    }

    [Fact]
    public async Task PreCanceledCall_DoesNotEnumerateMessagesOrStartPersistence()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        cancellation.Cancel();
        var messages = new SingleUseMessages([new ChatMessage(ChatRole.User, "Sort [3,1,2]")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            routing.GetResponseAsync(messages, cancellationToken: cancellation.Token));

        Assert.Equal(0, messages.Enumerations);
        Assert.Equal(0, RunCount(database));
        Assert.Equal(0, fast.Inner.Invocations + balanced.Inner.Invocations + strong.Inner.Invocations);
    }

    [Fact]
    public async Task ProviderCancellation_PropagatesAndNeverReselects()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestData.Ct);
        using var fast = new DelegateClient((_, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromCanceled<ChatResponse>(token);
        });
        using var balanced = new ScriptedChatClient(FixtureBehavior.Correct);
        using var strong = new ScriptedChatClient(FixtureBehavior.Correct);
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            [
                TestData.Route("fast", Tier.Fast, fast),
                TestData.Route("balanced", Tier.Balanced, balanced),
                TestData.Route("strong", Tier.Strong, strong)
            ],
            new OutcomeRoutingOptions { HistoryPath = database.Path });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: cancellation.Token));

        var runId = OnlyRunId(database);
        Assert.Equal(RunStatus.Cancelled, routing.GetRun(runId).Status);
        Assert.Null(routing.GetRun(runId).Feedback);
        Assert.Single(routing.GetAttempts(runId));
        Assert.Equal(1, fast.Invocations);
        Assert.Equal(0, balanced.Invocations + strong.Invocations);
    }

    [Fact]
    public async Task DeclaredJsonCapabilityAndMinimumTier_AreAppliedAtClientCreation()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            [
                ChatRoute.Create(Tier.Fast, fast, capabilities: Capability.Text | Capability.Json),
                ChatRoute.Create(Tier.Balanced, balanced, capabilities: Capability.Text | Capability.Json),
                ChatRoute.Create(Tier.Strong, strong, capabilities: Capability.Text | Capability.Json)
            ],
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                RequiredCapabilities = Capability.Text | Capability.Json,
                MinimumTier = Tier.Strong
            });
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        var result = Info(await routing.GetResponseAsync("Sort [3,1,2]", options, TestData.Ct));

        Assert.Equal(Tier.Fast, result.RecommendedTier);
        Assert.Equal(Tier.Strong, result.SelectedTier);
        Assert.Equal("strong", result.ActualRoute);
        Assert.Equal(0, fast.Inner.Invocations + balanced.Inner.Invocations);
        Assert.Equal(1, strong.Inner.Invocations);
        Assert.Same(ChatResponseFormat.Json, options.ResponseFormat);
        Assert.Same(ChatResponseFormat.Json, strong.Inner.ObservedOptions!.ResponseFormat);
        Assert.Equal(Outcome.Unknown, result.Feedback.Outcome);
    }

    [Fact]
    public async Task VerifierFactoryError_PropagatesBeforeAnyRunOrProviderInvocation()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(fast, balanced, strong),
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                VerifierFactory = _ => throw new InvalidOperationException("independent verifier setup failed")
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            routing.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal("independent verifier setup failed", error.Message);
        Assert.Equal(0, RunCount(database));
        Assert.Equal(0, fast.Inner.Invocations + balanced.Inner.Invocations + strong.Inner.Invocations);
    }

    [Theory]
    [InlineData("known", Outcome.Success)]
    [InlineData("different-input", Outcome.Unknown)]
    [InlineData("system-only", Outcome.Unknown)]
    [InlineData("additional-instructions", Outcome.Unknown)]
    public async Task DemoVerifierSelector_OnlyGradesItsIndependentlyKnownExactRequest(string request, Outcome expected)
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = new OutcomeRoutingChatClient(
            decisions,
            Routes(fast, balanced, strong),
            new OutcomeRoutingOptions
            {
                HistoryPath = database.Path,
                VerifierFactory = RoutingSamples.SortVerification.ForMessages
            });
        const string prompt = "Sort [3,1,2] ascending. Return only the JSON array.";
        ChatMessage[] messages = request switch
        {
            "known" => [new(ChatRole.User, prompt)],
            "different-input" => [new(ChatRole.User, "Sort [9,8,7] ascending. Return only the JSON array.")],
            "system-only" => [new(ChatRole.System, prompt)],
            "additional-instructions" => [new(ChatRole.System, "Return descending."), new(ChatRole.User, prompt)],
            _ => throw new InvalidOperationException()
        };

        var result = Info(await routing.GetResponseAsync(messages, cancellationToken: TestData.Ct));

        Assert.Equal(expected, result.Feedback.Outcome);
        if (expected == Outcome.Success)
        {
            Assert.Equal(TestData.Success, routing.GetRun(result.RunId).Feedback);
        }
        else
        {
            Assert.Null(routing.GetRun(result.RunId).Feedback);
        }
    }

    [Fact]
    public async Task OmittedVerifier_AfterFailoverReportsFeedbackAgainstCompletedAlternate()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model", FixtureBehavior.FailBeforeOutput);
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path, verify: false);
        using IChatClient client = routing.AsBuilder().Build();

        var first = Info(await client.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(Outcome.Unknown, first.Feedback.Outcome);
        Assert.Equal("balanced", first.ActualRoute);
        Assert.Null(routing.GetRun(first.RunId).Feedback);
        var feedback = new Feedback(Outcome.Failure, Provenance.Application, "independent-check");
        Assert.True(client.GetService<OutcomeRoutingChatClient>()!.ReportFeedback(first.RunId, feedback));
        Assert.Equal(first.Attempts[1].RouteIdentity, routing.GetRun(first.RunId).ActualRouteIdentity);
        Assert.Equal(feedback, routing.GetRun(first.RunId).Feedback);

        var next = Info(await client.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(Tier.Fast, next.RecommendedTier);
        Assert.Equal(Tier.Strong, next.SelectedTier);
        Assert.Equal("strong", next.ActualRoute);
        Assert.Equal(1, fast.Inner.Invocations);
        Assert.Equal(Outcome.Unknown, next.Feedback.Outcome);
    }

    [Fact]
    public async Task StreamingRepeatedEnumeration_HasIndependentRunsButSnapshotsMessagesOnlyOnce()
    {
        using var database = new TestDatabase();
        using var decisions = new FixtureDecisionGenerator();
        using var fast = new MetadataClient("fast-model");
        using var balanced = new MetadataClient("balanced-model");
        using var strong = new MetadataClient("strong-model");
        using var routing = Create(decisions, fast, balanced, strong, database.Path);
        var messages = new SingleUseMessages([new ChatMessage(ChatRole.User, "Sort [3,1,2]")]);
        var stream = routing.GetStreamingResponseAsync(messages, cancellationToken: TestData.Ct);

        for (int invocation = 0; invocation < 2; invocation++)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in stream)
            {
                updates.Add(update);
            }

            Assert.Equal("[1,2,3]", Assert.Single(updates).Text);
        }

        Assert.Equal(1, messages.Enumerations);
        Assert.Equal(2, RunCount(database));
        Assert.Equal(2, fast.Inner.Invocations);
        using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM runs r JOIN feedback f ON f.run_id=r.run_id
            WHERE r.status='Completed' AND f.outcome='Success' AND f.provenance='Verifier';
            """;
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private static OutcomeRoutingChatClient Create(
        IDecisionGenerator decisions,
        IChatClient fast,
        IChatClient balanced,
        IChatClient strong,
        string historyPath,
        bool verify = true)
    {
        return new OutcomeRoutingChatClient(
            decisions,
            [
                ChatRoute.Create(Tier.Fast, fast),
                ChatRoute.Create(Tier.Balanced, balanced),
                ChatRoute.Create(Tier.Strong, strong)
            ],
            new OutcomeRoutingOptions
            {
                HistoryPath = historyPath,
                Cohort = "sort",
                VerifierFactory = verify ? _ => TestData.Sort : null
            });
    }

    private static RoutingResponseInfo Info(ChatResponse response) =>
        Assert.IsType<RoutingResponseInfo>(response.AdditionalProperties![OutcomeRoutingChatClient.ResponseInfoKey]);

    private static Guid OnlyRunId(TestDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM runs;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Guid runId = Guid.Parse(reader.GetString(0));
        Assert.False(reader.Read());
        return runId;
    }

    private static long RunCount(TestDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM runs;";
        return (long)command.ExecuteScalar()!;
    }

    private static ChatRoute[] Routes(IChatClient fast, IChatClient balanced, IChatClient strong) =>
    [
        ChatRoute.Create(Tier.Fast, fast),
        ChatRoute.Create(Tier.Balanced, balanced),
        ChatRoute.Create(Tier.Strong, strong)
    ];

    private sealed class SingleUseMessages(IEnumerable<ChatMessage> messages) : IEnumerable<ChatMessage>
    {
        internal int Enumerations { get; private set; }

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            Enumerations++;
            if (Enumerations != 1)
            {
                throw new InvalidOperationException("A single-use message sequence was enumerated twice.");
            }

            return messages.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ContractClient(
        ChatResponse? response = null,
        IReadOnlyList<ChatResponseUpdate>? updates = null,
        bool failAfterOutput = false) : IChatClient
    {
        internal IReadOnlyList<ChatMessage>? Messages { get; private set; }
        internal ChatOptions? Options { get; private set; }
        internal int Invocations { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Observe(messages, options, cancellationToken);
            return Task.FromResult(response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "[1,2,3]")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Observe(messages, options, cancellationToken);
            await Task.CompletedTask;
            foreach (var update in updates ?? [new ChatResponseUpdate(ChatRole.Assistant, "[1,2,3]")])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                cancellationToken.ThrowIfCancellationRequested();
                if (failAfterOutput)
                {
                    throw new HttpRequestException("simulated failure after committed output");
                }
            }
        }

        private void Observe(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            Messages = messages.ToArray();
            Options = options;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("fixture", new Uri("fixture://local"), "contract-model")
                : null;

        public void Dispose()
        {
        }
    }

    private sealed class MetadataClient(
        string? model,
        FixtureBehavior behavior = FixtureBehavior.Correct,
        string? endpoint = "fixture://local") : IChatClient
    {
        internal ScriptedChatClient Inner { get; } = new(behavior);
        internal int DisposeCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(ChatClientMetadata) && serviceKey is null
                ? new ChatClientMetadata("fixture", endpoint is null ? null : new Uri(endpoint), model)
                : null;
        }

        public void Dispose()
        {
            DisposeCount++;
            Inner.Dispose();
        }
    }

    private sealed class OwnedGenerator : IDecisionGenerator
    {
        private readonly FixtureDecisionGenerator _inner = new();
        internal bool Disposed { get; private set; }

        public Task<DecisionResult> GenerateAsync(
            DecisionInput input,
            DecisionGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.GenerateAsync(input, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }
    }
}

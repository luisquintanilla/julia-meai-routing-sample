using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using RoutingSamples;

namespace OutcomeRouting.Tests;

public sealed class OllamaRoutingTests
{
    [Fact]
    public void Adapter_ImplementsIChatClientAndExposesExactMetadataWithoutNetwork()
    {
        var settings = Settings();
        using IChatClient client = new OllamaApiClient(settings.Endpoint, settings.FastModel);
        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService<ChatClientMetadata>());
        var route = ChatRoute.Create(Tier.Fast, client);
        var explicitRoute = new ChatRoute(
            "fast", Tier.Fast, Capability.Text, settings.Endpoint.AbsoluteUri, settings.FastModel, "v1", client);

        Assert.Equal("ollama", metadata.ProviderName);
        Assert.Equal(settings.Endpoint, metadata.ProviderUri);
        Assert.Equal(settings.FastModel, metadata.DefaultModelId);
        Assert.Equal(settings.FastModel, route.Model);
        Assert.Equal(explicitRoute.Identity, route.Identity);
        Assert.Same(client, client.GetService<IOllamaApiClient>());
        Assert.False(settings.Stream);
    }

    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("http://localhost:11434/api/")]
    [InlineData("http://localhost:11434/?key=value")]
    [InlineData("http://localhost:11434/#fragment")]
    [InlineData("http://user:password@localhost:11434/")]
    [InlineData("file://localhost/")]
    [InlineData("not-a-uri")]
    public void Configuration_RejectsNonLocalOrSensitiveEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaSettings.Parse([endpoint, "fast:local", "balanced:local", "strong:local"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("model")]
    [InlineData("model:tag cloud")]
    [InlineData("model:tag-cloud")]
    [InlineData("model:?key")]
    public void Configuration_RejectsMissingInexactOrCloudModelTags(string model)
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaSettings.Parse(["http://localhost:11434/", model, "balanced:local", "strong:local"]));
    }

    [Fact]
    public void Configuration_AcceptsExplicitLocalMappingAndStreamingButRejectsUnknownFlags()
    {
        var settings = OllamaSettings.Parse(
            ["http://127.0.0.1:11434/", "family/fast:small", "balanced:medium", "strong:large", "--stream"]);

        Assert.Equal(new Uri("http://127.0.0.1:11434/"), settings.Endpoint);
        Assert.Equal("family/fast:small", settings.FastModel);
        Assert.Equal("balanced:medium", settings.BalancedModel);
        Assert.Equal("strong:large", settings.StrongModel);
        Assert.True(settings.Stream);
        Assert.Throws<ArgumentException>(() => OllamaSettings.Parse([]));
        Assert.Throws<ArgumentException>(() =>
            OllamaSettings.Parse(["http://localhost:11434/", "a:1", "b:1", "c:1", "--download"]));
        Assert.Throws<ArgumentException>(() =>
            OllamaSettings.Parse(["http://localhost:11434/", new string('a', 65) + ":1", "b:1", "c:1"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preflight_ListsExactInstalledTagsAndNeverPullsOrChats(bool allInstalled)
    {
        var settings = Settings();
        using var handler = new OfflineHandler((_, _) =>
            Task.FromResult(Json(allInstalled
                ? """{"models":[{"name":"fast:local"},{"name":"balanced:local"},{"name":"strong:local"}]}"""
                : """{"models":[{"name":"fast:local"}]}""")));
        using var http = new HttpClient(handler) { BaseAddress = settings.Endpoint };
        using var client = new OllamaApiClient(http, settings.FastModel);

        if (allInstalled)
        {
            await settings.EnsureModelsAvailableAsync(client, TestData.Ct);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                settings.EnsureModelsAvailableAsync(client, TestData.Ct));
            Assert.Contains("balanced:local, strong:local", error.Message);
            Assert.Contains("Nothing was downloaded.", error.Message);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/tags", request.Uri.AbsolutePath);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Preflight_UnavailableEndpointPropagatesWithoutDownloadOrFallback()
    {
        var settings = Settings();
        var expected = new HttpRequestException("simulated unreachable endpoint");
        using var handler = new OfflineHandler((_, _) => throw expected);
        using var http = new HttpClient(handler) { BaseAddress = settings.Endpoint };
        using var client = new OllamaApiClient(http, settings.FastModel);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            settings.EnsureModelsAvailableAsync(client, TestData.Ct));

        Assert.Same(expected, error);
        Assert.Equal("/api/tags", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task RealAdapter_WithOfflineHttpPreservesHistoryOptionsResponseAndStructuredRouting()
    {
        using var database = new TestDatabase();
        var settings = Settings();
        using var handler = new OfflineHandler((_, _) => Task.FromResult(Json(
            """{"model":"fast:local","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"[1,2,3]"},"done":true,"done_reason":"stop","prompt_eval_count":13,"eval_count":4}""")));
        using var http = new HttpClient(handler) { BaseAddress = settings.Endpoint };
        using IChatClient ollama = new OllamaApiClient(http, settings.FastModel);
        using var decisions = new FixtureDecisionGenerator();
        var observer = new RecordingRoutingObserver();
        using IChatClient routing = Create(database, decisions, ollama, observer)
            .AsBuilder()
            .ConfigureOptions(options => options.TopP = 0.9f)
            .Build();
        var callerOptions = new ChatOptions { TopP = 0.25f, ModelId = "caller-model" };

        ChatResponse response = await routing.GetResponseAsync(
            [new(ChatRole.System, " Return JSON.\n"), new(ChatRole.User, "Sort [3,1,2]")],
            callerOptions,
            TestData.Ct);

        Assert.Equal("[1,2,3]", response.Text);
        Assert.Equal("fast:local", response.ModelId);
        Assert.Equal(13, response.Usage!.InputTokenCount);
        Assert.Equal(4, response.Usage.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(0.25f, callerOptions.TopP);
        Assert.Equal("caller-model", callerOptions.ModelId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/chat", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        var json = body.RootElement;
        Assert.Equal("fast:local", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal("system", json.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(" Return JSON.\n", json.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("user", json.GetProperty("messages")[1].GetProperty("role").GetString());
        Assert.Equal("Sort [3,1,2]", json.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(0, json.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(128, json.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0.9f, json.GetProperty("options").GetProperty("top_p").GetSingle());
        Assert.DoesNotContain("outcome-routing-request", request.Body!);
        var info = Assert.IsType<RoutingResponseInfo>(response.AdditionalProperties![OutcomeRoutingChatClient.ResponseInfoKey]);
        var records = observer.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(info.RunId, record.RunId));
        Assert.Equal("fast", records[1].Route);
        Assert.True(records[1].Attempt!.ResponseCompleted);
        Assert.Equal(TestData.Success, info.Feedback);
    }

    [Fact]
    public async Task RealAdapterStreaming_WithOfflineHttpForwardsTextAndUsageWithoutSyntheticObservationUpdates()
    {
        using var database = new TestDatabase();
        var settings = Settings();
        using var handler = new OfflineHandler((_, _) => Task.FromResult(Json(
            """
            {"model":"fast:local","message":{"role":"assistant","content":"[1,"},"done":false}
            {"model":"fast:local","message":{"role":"assistant","content":"2,3]"},"done":true,"done_reason":"stop","prompt_eval_count":13,"eval_count":4}
            """ + "\n")));
        using var http = new HttpClient(handler) { BaseAddress = settings.Endpoint };
        using IChatClient ollama = new OllamaApiClient(http, settings.FastModel);
        using var decisions = new FixtureDecisionGenerator();
        var observer = new RecordingRoutingObserver();
        using IChatClient routing = Create(database, decisions, ollama, observer);
        List<ChatResponseUpdate> updates = [];

        await foreach (var update in routing.GetStreamingResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("[1,", updates[0].Text);
        Assert.Equal("2,3]", updates[1].Text);
        Assert.Equal(ChatFinishReason.Stop, updates[1].FinishReason);
        var usage = Assert.Single(updates[1].Contents.OfType<UsageContent>());
        Assert.Equal(13, usage.Details.InputTokenCount);
        Assert.Equal(4, usage.Details.OutputTokenCount);
        Assert.All(updates, update => Assert.False(update.AdditionalProperties?.ContainsKey(OutcomeRoutingChatClient.ResponseInfoKey) == true));
        Assert.Equal(2, observer.Snapshot().Count);
        Assert.True(observer.Snapshot()[1].Attempt!.OutputCommitted);
        var runId = observer.Snapshot()[0].RunId;
        Assert.Equal(RunStatus.Completed, ((OutcomeRoutingChatClient)routing).GetRun(runId).Status);
        Assert.Equal(TestData.Success, ((OutcomeRoutingChatClient)routing).GetRun(runId).Feedback);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task MissingModelDuringChat_FailsRatherThanReturningAnEmptySuccess()
    {
        var settings = Settings();
        using var handler = new OfflineHandler((_, _) =>
            Task.FromResult(Json("""{"error":"model unavailable"}""", HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler) { BaseAddress = settings.Endpoint };
        using IChatClient client = new OllamaApiClient(http, settings.FastModel);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync("Sort [3,1,2]", cancellationToken: TestData.Ct));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("/api/chat", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    private static OllamaSettings Settings() =>
        OllamaSettings.Parse(["http://localhost:11434/", "fast:local", "balanced:local", "strong:local"]);

    private static OutcomeRoutingChatClient Create(
        TestDatabase database,
        FixtureDecisionGenerator decisions,
        IChatClient ollama,
        IRoutingObserver observer)
    {
        return new OutcomeRoutingChatClient(
            decisions,
            [
                ChatRoute.Create(Tier.Fast, ollama),
                ChatRoute.Create(Tier.Balanced, ollama),
                ChatRoute.Create(Tier.Strong, ollama)
            ],
            new OutcomeRoutingOptions
            {
                History = new SqliteOutcomeStore(database.Path),
                VerifierFactory = _ => TestData.Sort,
                Observer = observer
            });
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class OfflineHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return await response(request, cancellationToken);
        }
    }
}

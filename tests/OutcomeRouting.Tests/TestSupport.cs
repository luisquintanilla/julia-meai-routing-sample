global using OutcomeRouting;
global using Xunit;
using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace OutcomeRouting.Tests;

internal static class TestData
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static readonly ExactSortVerifier Sort = new([3, 1, 2]);
    internal static RoutingTask Task(string cohort = "sort", Tier minimum = Tier.Fast) =>
        new(cohort, "Sort [3,1,2]", minimumTier: minimum);
    internal static Feedback Success => new(Outcome.Success, Provenance.Verifier, "exact-sort-v1");
    internal static Feedback Failure => new(Outcome.Failure, Provenance.Verifier, "exact-sort-v1");

    internal static ChatRoute Route(string name, Tier tier, IChatClient? client = null,
        string? model = null, string endpoint = "fixture://local", string revision = "v1",
        RouteSettings? settings = null, Capability capabilities = Capability.Text | Capability.Json) =>
        new(name, tier, capabilities, endpoint, model ?? $"{DecisionPolicy.Id(tier)}-model", revision,
            client ?? new ScriptedChatClient(FixtureBehavior.Correct), settings);

    internal static DecisionSnapshot Decision(Tier tier = Tier.Fast) =>
        new("fixture-not-julia", Tier.Fast,
            new Dictionary<string, double> { ["fast"] = .9, ["balanced"] = .08, ["strong"] = .02 },
            tier, ["decision-signal"], "{}", null);

    internal static Guid Seed(IOutcomeStore store, RouteCatalog catalog, ChatRoute route,
        Feedback? feedback = null, string cohort = "sort")
    {
        var task = Task(cohort);
        store.Begin(task, catalog.Revision);
        store.RecordDecision(task.RunId, Decision(route.Tier));
        store.RecordAttempt(task.RunId, new(1, route.Name, route.Identity, route.Tier, 0, null, true, false, null));
        store.Finish(task.RunId, RunStatus.Completed, route.Identity, feedback);
        return task.RunId;
    }
}

internal sealed class TestDatabase : IDisposable
{
    internal string DirectoryPath { get; }
    internal string Path { get; }

    internal TestDatabase([CallerFilePath] string source = "")
    {
        // This compiled source is under this worktree/tests/OutcomeRouting.Tests.
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(source)!, "..", ".."));
        DirectoryPath = System.IO.Path.Combine(root, ".routing", $"tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        Path = System.IO.Path.Combine(DirectoryPath, "outcomes.db");
    }

    internal void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Only this exact fixture-owned directory; never any other .routing data.
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class TestRig : IDisposable
{
    internal TestDatabase Database { get; } = new();
    internal SqliteOutcomeStore Store { get; }
    internal ScriptedChatClient Fast { get; }
    internal ScriptedChatClient Balanced { get; }
    internal ScriptedChatClient Strong { get; }
    internal RouteCatalog Catalog { get; }
    internal OutcomeRouter Router { get; }
    internal OutcomeApplication App { get; }

    internal TestRig(FixtureBehavior fast = FixtureBehavior.Correct,
        FixtureBehavior balanced = FixtureBehavior.Correct, FixtureBehavior strong = FixtureBehavior.Correct,
        PolicySettings? settings = null, IDecisionGenerator? generator = null,
        IChatClient? fastClient = null, IChatClient? balancedClient = null,
        Func<IOutcomeStore, IOutcomeStore>? decorate = null)
    {
        Store = new(Database.Path);
        Fast = new(fast);
        Balanced = new(balanced);
        Strong = new(strong);
        Catalog = new([TestData.Route("fast", Tier.Fast, fastClient ?? Fast),
            TestData.Route("balanced", Tier.Balanced, balancedClient ?? Balanced),
            TestData.Route("strong", Tier.Strong, Strong)]);
        var store = decorate?.Invoke(Store) ?? Store;
        Router = new(generator ?? new FixtureDecisionGenerator(), Catalog, settings ?? new(), store);
        App = new(Router, Catalog, store);
    }

    internal Task<ExecutionResult> Execute(RoutingTask? task = null, ITaskVerifier? verifier = null) =>
        App.ExecuteAsync(task ?? TestData.Task(), verifier ?? TestData.Sort, cancellationToken: TestData.Ct);

    public void Dispose() => Database.Dispose();
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class DelegateGenerator(
    Func<DecisionInput, CancellationToken, Task<DecisionResult>> generate) : IDecisionGenerator
{
    internal int Invocations { get; private set; }
    internal DecisionInput? Input { get; private set; }
    public Task<DecisionResult> GenerateAsync(DecisionInput input, DecisionGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Invocations++;
        Input = input;
        return generate(input, cancellationToken);
    }
    public object? GetService(Type type, object? key = null) => null;
    public void Dispose() { }
}

internal sealed class DelegateClient(
    Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> response,
    Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? stream = null) : IChatClient
{
    internal int Invocations { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Invocations++;
        return response(messages, options, cancellationToken);
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Invocations++;
        return stream!(cancellationToken);
    }
    public object? GetService(Type type, object? key = null) => null;
    public void Dispose() { }
}

internal sealed class CountingVerifier(ITaskVerifier inner) : ITaskVerifier
{
    internal int Invocations { get; private set; }
    public Feedback Verify(ChatResponse response)
    {
        Invocations++;
        return inner.Verify(response);
    }
}

internal sealed class UnknownVerifier : ITaskVerifier
{
    public Feedback Verify(ChatResponse response) => Feedback.Unknown("explicit-unknown");
}

internal sealed class NullVerifier : ITaskVerifier
{
    internal int Invocations { get; private set; }
    public Feedback Verify(ChatResponse response)
    {
        Invocations++;
        return null!; // intentionally broken supplied verifier, not an omitted verifier
    }
}

internal sealed class FaultStore(IOutcomeStore inner, string operation) : IOutcomeStore
{
    private void Check(string name)
    {
        if (operation == name) throw new IOException($"fixture-{name}");
    }
    public void Begin(RoutingTask task, string revision) => inner.Begin(task, revision);
    public IReadOnlyList<Evidence> ReadEvidence(string cohort, string revision, PolicySettings settings)
    { Check(nameof(ReadEvidence)); return inner.ReadEvidence(cohort, revision, settings); }
    public void RecordDecision(Guid id, DecisionSnapshot decision)
    { Check(nameof(RecordDecision)); inner.RecordDecision(id, decision); }
    public void RecordAttempt(Guid id, AttemptRecord attempt)
    { Check(nameof(RecordAttempt)); inner.RecordAttempt(id, attempt); }
    public void Finish(Guid id, RunStatus status, string? actual, Feedback? feedback) => inner.Finish(id, status, actual, feedback);
    public bool ReportFeedback(Guid id, string actual, Feedback feedback) => inner.ReportFeedback(id, actual, feedback);
    public RunRecord GetRun(Guid id) => inner.GetRun(id);
    public IReadOnlyList<AttemptRecord> GetAttempts(Guid id) => inner.GetAttempts(id);
}

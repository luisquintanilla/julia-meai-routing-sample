using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace OutcomeRouting.Tests;

public sealed class OutcomeStoreTests
{
    [Fact]
    public void LoadedSqliteRuntime_UsesPatchedNativeBuild()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        string loaded = Assert.IsType<string>(command.ExecuteScalar());
        Assert.True(Version.Parse(loaded) >= new Version(3, 50, 2),
            $"Loaded SQLite {loaded} is below the CVE-2025-6965 fix in 3.50.2.");
        Assert.Equal(connection.ServerVersion, loaded);
    }

    [Theory]
    [InlineData(Tier.Fast, Tier.Balanced)]
    [InlineData(Tier.Balanced, Tier.Strong)]
    [InlineData(Tier.Strong, Tier.Strong)]
    public async Task VerifiedFailure_ChangesSameCohortSelectionAtActualTier(Tier actual, Tier next)
    {
        using var rig = new TestRig(FixtureBehavior.Incorrect, FixtureBehavior.Incorrect, FixtureBehavior.Incorrect);
        var result = await rig.Execute(TestData.Task(minimum: actual));
        var completed = rig.Catalog.Routes.Single(r => r.Name == result.ActualRoute);
        Assert.Equal(TestData.Failure, result.Feedback); // real independent ExactSortVerifier
        var followup = await rig.Execute();
        Assert.Equal(Tier.Fast, followup.Decision.Recommended); // fixture still recommends Fast
        Assert.Equal(next, followup.Decision.Selected);
        Assert.Contains($"recent-verified-failure:{completed.Name}:minimum-{DecisionPolicy.Id(next)}",
            followup.Decision.Reasons);
        Assert.Equal(Outcome.Failure, rig.Store.GetRun(result.RunId).Feedback!.Outcome);
        Assert.Equal(completed.Identity, rig.Store.GetRun(result.RunId).ActualRouteIdentity);
        var evidence = rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new());
        Assert.Contains(evidence, e => e.RouteIdentity == completed.Identity && e.Tier == actual && e.Outcome == Outcome.Failure);
        var unrelated = await rig.Execute(TestData.Task("other-cohort"));
        Assert.Equal(Tier.Fast, unrelated.Decision.Selected);
    }

    [Fact]
    public async Task IndependentSortFailure_OverridesFixtureFastRecommendation()
    {
        using var rig = new TestRig(fast: FixtureBehavior.Incorrect);
        var first = await rig.Execute();
        var next = await rig.Execute();
        Assert.Equal(Outcome.Failure, first.Feedback.Outcome);
        Assert.Equal(Provenance.Verifier, first.Feedback.Provenance);
        Assert.Equal("fast", first.ActualRoute);
        Assert.Equal(Tier.Fast, next.Decision.Recommended);
        Assert.Equal(Tier.Balanced, next.Decision.Selected);
        Assert.Equal("balanced", next.ActualRoute);
        Assert.Equal(Outcome.Success, next.Feedback.Outcome);
    }

    [Fact]
    public async Task OmittedVerifier_IsTransportOnlyUnknownAndAllowsLaterFeedback()
    {
        using var rig = new TestRig(fast: FixtureBehavior.Incorrect);
        var task = TestData.Task();
        var result = await rig.App.ExecuteAsync(task, null, cancellationToken: TestData.Ct);
        Assert.Equal(Outcome.Unknown, result.Feedback.Outcome);
        Assert.Equal(RunStatus.Completed, rig.Store.GetRun(task.RunId).Status);
        Assert.Null(rig.Store.GetRun(task.RunId).Feedback); // no terminal feedback row
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        var next = await rig.App.ExecuteAsync(TestData.Task(), null, cancellationToken: TestData.Ct);
        Assert.Equal(Tier.Fast, next.Decision.Selected);
        Assert.True(rig.Store.ReportFeedback(task.RunId, rig.Catalog.Routes[0].Identity, TestData.Failure));
        var afterFeedback = await rig.Execute();
        Assert.Equal(Tier.Balanced, afterFeedback.Decision.Selected);
        Assert.Equal(TestData.Failure, rig.Store.GetRun(task.RunId).Feedback);
    }

    [Theory]
    [InlineData(Provenance.Application, "application")]
    [InlineData(Provenance.Verifier, "verified")]
    public async Task FailureReason_PreservesIndependentFeedbackProvenance(Provenance provenance, string prefix)
    {
        using var fixture = new FixtureDecisionGenerator();
        using var generator = new DelegateGenerator((input, ct) => fixture.GenerateAsync(input, cancellationToken: ct));
        using var rig = new TestRig(generator: generator);
        var first = await rig.App.ExecuteAsync(TestData.Task(), null, cancellationToken: TestData.Ct);
        var feedback = new Feedback(Outcome.Failure, provenance, "independent-check");
        Assert.True(rig.Store.ReportFeedback(first.RunId, rig.Catalog.Routes[0].Identity, feedback));
        var next = await rig.Execute();
        Assert.Equal(Tier.Fast, next.Decision.Recommended);
        Assert.Equal(Tier.Balanced, next.Decision.Selected);
        Assert.Equal("balanced", next.ActualRoute);
        Assert.Equal($"recent-{prefix}-failure:fast:minimum-balanced", Assert.Single(next.Decision.Reasons));
        Assert.Equal(feedback, rig.Store.GetRun(first.RunId).Feedback);
        Assert.Contains(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()),
            e => e.Outcome == Outcome.Failure && e.Provenance == provenance && e.RouteIdentity == rig.Catalog.Routes[0].Identity);
        using var projection = JsonDocument.Parse(next.Decision.ProjectedState);
        // Pin both the caller-visible projection and the actual model input,
        // not just the persisted row or human-facing policy reason.
        foreach (var state in new[] { projection.RootElement, generator.Input!.State })
        {
            var entry = Assert.Single(state.GetProperty("Evidence").EnumerateArray());
            Assert.Equal("fast", entry.GetProperty("Route").GetString());
            Assert.Equal("fast", entry.GetProperty("Tier").GetString());
            Assert.Equal("Failure", entry.GetProperty("Outcome").GetString());
            Assert.Equal(provenance.ToString(), entry.GetProperty("Provenance").GetString());
        }
    }

    [Fact]
    public async Task ExplicitUnknown_RemainsImmutableAndNeverPromotesSelection()
    {
        using var rig = new TestRig(fast: FixtureBehavior.Incorrect);
        var result = await rig.Execute(verifier: new UnknownVerifier());
        var unknown = Feedback.Unknown("explicit-unknown");
        var actual = rig.Catalog.Routes[0].Identity;
        Assert.Equal(unknown, rig.Store.GetRun(result.RunId).Feedback);
        Assert.False(rig.Store.ReportFeedback(result.RunId, actual, unknown));
        Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(result.RunId, actual, TestData.Failure));
        Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(result.RunId, actual, TestData.Success));
        Assert.Equal(unknown, rig.Store.GetRun(result.RunId).Feedback);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        var next = await rig.Execute(verifier: new UnknownVerifier());
        Assert.Equal(Tier.Fast, next.Decision.Selected);
    }

    [Fact]
    public async Task Feedback_IdenticalIsIdempotentAndConflictsAreRejected()
    {
        using var rig = new TestRig();
        var result = await rig.App.ExecuteAsync(TestData.Task(), null, cancellationToken: TestData.Ct);
        var actual = rig.Catalog.Routes[0].Identity;
        Assert.True(rig.Store.ReportFeedback(result.RunId, actual, TestData.Success));
        Assert.False(rig.Store.ReportFeedback(result.RunId, actual, TestData.Success));
        Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(result.RunId, actual, TestData.Failure));
        Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(result.RunId, actual,
            new(Outcome.Success, Provenance.Application, "different-source")));
        Assert.Equal(TestData.Success, rig.Store.GetRun(result.RunId).Feedback);
        Assert.Equal(new Evidence("fast", actual, Tier.Fast, Outcome.Success, Provenance.Verifier),
            Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new())));
    }

    [Fact]
    public async Task Feedback_UnknownNoncompletedForeignAndWrongActualRouteAreRejected()
    {
        using var rig = new TestRig();
        var completed = await rig.App.ExecuteAsync(TestData.Task(), null, cancellationToken: TestData.Ct);
        var actual = rig.Catalog.Routes[0].Identity;
        Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(Guid.NewGuid(), actual, TestData.Success));
        Assert.Throws<ArgumentException>(() => rig.Store.ReportFeedback(Guid.Empty, actual, TestData.Success));
        foreach (var status in new[] { RunStatus.Running, RunStatus.Failed, RunStatus.Cancelled, RunStatus.Abandoned })
        {
            var task = TestData.Task();
            rig.Store.Begin(task, rig.Catalog.Revision);
            if (status != RunStatus.Running) rig.Store.Finish(task.RunId, status, null, null);
            Assert.Throws<InvalidOperationException>(() => rig.Store.ReportFeedback(task.RunId, actual, TestData.Success));
            Assert.Null(rig.Store.GetRun(task.RunId).Feedback);
        }
        Assert.Throws<InvalidOperationException>(() =>
            rig.Store.ReportFeedback(completed.RunId, rig.Catalog.Routes[1].Identity, TestData.Success));
        using var foreignDb = new TestDatabase();
        var foreign = new SqliteOutcomeStore(foreignDb.Path);
        Assert.Throws<InvalidOperationException>(() => foreign.ReportFeedback(completed.RunId, actual, TestData.Success));
        Assert.Throws<KeyNotFoundException>(() => foreign.GetRun(completed.RunId));
        Assert.Null(rig.Store.GetRun(completed.RunId).Feedback);
        Assert.Empty(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
    }

    [Fact]
    public async Task Persistence_ReopenedAndAlreadyOpenStoresSeeNewerEvidence()
    {
        using var rig = new TestRig(fast: FixtureBehavior.Incorrect);
        var observer = new SqliteOutcomeStore(rig.Database.Path);
        Assert.Empty(observer.ReadEvidence("sort", rig.Catalog.Revision, new()));
        var result = await rig.Execute();
        var reloaded = new SqliteOutcomeStore(rig.Database.Path);
        foreach (var store in new[] { observer, reloaded })
        {
            Assert.Equal(rig.Store.GetRun(result.RunId), store.GetRun(result.RunId));
            Assert.Equal(result.Attempts, store.GetAttempts(result.RunId));
            Assert.Equal(Outcome.Failure, Assert.Single(store.ReadEvidence("sort", rig.Catalog.Revision, new())).Outcome);
        }
        var policy = new DecisionPolicy(new FixtureDecisionGenerator(), rig.Catalog, new());
        var decision = await policy.DecideAsync(TestData.Task(),
            observer.ReadEvidence("sort", rig.Catalog.Revision, new()), TestData.Ct);
        Assert.Equal(Tier.Balanced, decision.Selected);
    }

    [Fact]
    public async Task ConcurrentIndependentStores_SerializeDuplicateAndDistinctFeedback()
    {
        using var rig = new TestRig();
        var ids = Enumerable.Range(0, 3).Select(_ => TestData.Seed(rig.Store, rig.Catalog, rig.Catalog.Routes[0])).ToArray();
        var stores = Enumerable.Range(0, 6).Select(_ => new SqliteOutcomeStore(rig.Database.Path)).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;
        var writes = stores.Select((store, i) => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == stores.Length) ready.SetResult();
            await release.Task.WaitAsync(TestData.Ct);
            return (Id: ids[i / 2], Added: store.ReportFeedback(ids[i / 2], rig.Catalog.Routes[0].Identity,
                i / 2 == 1 ? TestData.Failure : TestData.Success));
        }, TestData.Ct)).ToArray();
        await ready.Task.WaitAsync(TestData.Ct);
        release.SetResult();
        var results = await Task.WhenAll(writes);
        Assert.Equal(3, results.Count(r => r.Added));
        foreach (var id in ids)
        {
            Assert.Equal(1, results.Count(r => r.Id == id && r.Added));
            Assert.Equal(id == ids[1] ? TestData.Failure : TestData.Success, rig.Store.GetRun(id).Feedback);
            Assert.Equal(rig.Catalog.Routes[0].Identity, rig.Store.GetRun(id).ActualRouteIdentity);
            Assert.Single(rig.Store.GetAttempts(id));
        }
        Assert.Equal(3, rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()).Count);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("revision")]
    [InlineData("reasoning")]
    [InlineData("temperature")]
    [InlineData("budget")]
    [InlineData("name")]
    [InlineData("capabilities")]
    public async Task ChangedRouteConfiguration_IsolatesEvidence(string change)
    {
        using var rig = new TestRig(fast: FixtureBehavior.Incorrect);
        await rig.Execute();
        var changed = TestData.Route(change == "name" ? "newfast" : "fast", Tier.Fast,
            model: change == "model" ? "new-model" : "fast-model",
            endpoint: change == "endpoint" ? "fixture://new-endpoint" : "fixture://local",
            revision: change == "revision" ? "v2" : "v1",
            settings: change switch {
                "reasoning" => new(Reasoning: "high"), "temperature" => new(.5),
                "budget" => new(MaximumOutputTokens: 256), _ => new() },
            capabilities: change == "capabilities" ? Capability.Text : Capability.Text | Capability.Json);
        var catalog = new RouteCatalog([changed, rig.Catalog.Routes[1], rig.Catalog.Routes[2]]);
        Assert.NotEqual(rig.Catalog.Routes[0].Identity, changed.Identity);
        Assert.NotEqual(rig.Catalog.Revision, catalog.Revision);
        Assert.Empty(rig.Store.ReadEvidence("sort", catalog.Revision, new()));
        var decision = await new DecisionPolicy(new FixtureDecisionGenerator(), catalog, new())
            .DecideAsync(TestData.Task(), rig.Store.ReadEvidence("sort", catalog.Revision, new()), TestData.Ct);
        Assert.Equal(Tier.Fast, decision.Selected);
        Assert.Equal(Outcome.Failure, Assert.Single(rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new())).Outcome);
        Assert.Equal(rig.Catalog.Revision, new RouteCatalog(rig.Catalog.Routes.Reverse()).Revision);
        Assert.Equal(changed.Identity, TestData.Route(changed.Name, Tier.Fast,
            model: changed.Model, endpoint: change == "endpoint" ? "fixture://new-endpoint" : "fixture://local",
            revision: change == "revision" ? "v2" : "v1",
            settings: change switch { "reasoning" => new(Reasoning: "high"),
                "temperature" => new(.5), "budget" => new(MaximumOutputTokens: 256), _ => new() },
            capabilities: changed.Capabilities).Identity);
    }

    [Fact]
    public void EvidenceLimitAndAgeWindow_UseRecentEligibleRowsAndInclusiveCutoff()
    {
        using var rig = new TestRig();
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now - TimeSpan.FromDays(7) - TimeSpan.FromMilliseconds(1));
        var store = new SqliteOutcomeStore(rig.Database.Path, clock);
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[0], TestData.Failure);
        clock.Now = now - TimeSpan.FromDays(7);
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[1], TestData.Failure);
        clock.Now = now;
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[2], TestData.Success);
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[0], TestData.Success);
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[0], Feedback.Unknown());
        TestData.Seed(store, rig.Catalog, rig.Catalog.Routes[0], TestData.Failure, "foreign-cohort");
        var changed = new RouteCatalog([TestData.Route("changed", Tier.Fast),
            rig.Catalog.Routes[1], rig.Catalog.Routes[2]]);
        TestData.Seed(store, changed, changed.Routes[0], TestData.Failure);
        var failed = TestData.Task();
        store.Begin(failed, rig.Catalog.Revision);
        store.Finish(failed.RunId, RunStatus.Failed, null, null);
        var limited = store.ReadEvidence("sort", rig.Catalog.Revision, new() { EvidenceLimit = 2 });
        Assert.Equal(["fast", "strong"], limited.Select(e => e.Route));
        Assert.All(limited, e => Assert.Equal(Outcome.Success, e.Outcome));
        var window = store.ReadEvidence("sort", rig.Catalog.Revision, new() { EvidenceLimit = 6 });
        Assert.Equal(["fast", "strong", "balanced"], window.Select(e => e.Route));
        Assert.Equal(Outcome.Failure, window[2].Outcome); // exactly at cutoff survives
        clock.Now = now + TimeSpan.FromDays(7) + TimeSpan.FromMilliseconds(1);
        Assert.Empty(store.ReadEvidence("sort", rig.Catalog.Revision, new() { EvidenceLimit = 6 }));
    }

    [Fact]
    public async Task EvidenceRecency_UsesCompletionTimeThenSequenceForOverlappingRuns()
    {
        using var rig = new TestRig();
        var epoch = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(epoch);
        var store = new SqliteOutcomeStore(rig.Database.Path, clock);

        Guid Start(ChatRoute route)
        {
            var task = TestData.Task();
            store.Begin(task, rig.Catalog.Revision);
            store.RecordDecision(task.RunId, TestData.Decision(route.Tier));
            store.RecordAttempt(task.RunId, new(1, route.Name, route.Identity, route.Tier, 0, null, true, false, null));
            return task.RunId;
        }

        // All three durable runs overlap. The first-created run finishes last.
        var firstCreated = Start(rig.Catalog.Routes[0]);
        clock.Now = epoch.AddMilliseconds(1);
        var secondCreated = Start(rig.Catalog.Routes[1]);
        clock.Now = epoch.AddMilliseconds(2);
        var thirdCreated = Start(rig.Catalog.Routes[2]);
        Assert.Empty(store.ReadEvidence("sort", rig.Catalog.Revision, new()));

        clock.Now = epoch.AddMilliseconds(10);
        store.Finish(secondCreated, RunStatus.Completed, rig.Catalog.Routes[1].Identity, TestData.Success);
        Assert.Equal("balanced", Assert.Single(store.ReadEvidence("sort", rig.Catalog.Revision, new())).Route);
        Assert.Equal(RunStatus.Running, store.GetRun(firstCreated).Status);

        clock.Now = epoch.AddMilliseconds(20);
        store.Finish(thirdCreated, RunStatus.Completed, rig.Catalog.Routes[2].Identity, TestData.Success);
        // Same finished_ms as thirdCreated: sequence DESC is the deterministic tie-breaker,
        // even though this Finish call happens later.
        store.Finish(firstCreated, RunStatus.Completed, rig.Catalog.Routes[0].Identity, TestData.Failure);
        var expected = new[] {
            new Evidence("strong", rig.Catalog.Routes[2].Identity, Tier.Strong, Outcome.Success, Provenance.Verifier),
            new Evidence("fast", rig.Catalog.Routes[0].Identity, Tier.Fast, Outcome.Failure, Provenance.Verifier),
            new Evidence("balanced", rig.Catalog.Routes[1].Identity, Tier.Balanced, Outcome.Success, Provenance.Verifier) };
        Assert.Equal(expected, store.ReadEvidence("sort", rig.Catalog.Revision, new()));
        var limited = store.ReadEvidence("sort", rig.Catalog.Revision, new() { EvidenceLimit = 2 });
        Assert.Equal(expected.Take(2), limited); // creation-order LIMIT would incorrectly discard Fast failure
        var newest = store.ReadEvidence("sort", rig.Catalog.Revision, new() { EvidenceLimit = 1 });
        Assert.Equal(expected[0], Assert.Single(newest));
        foreach (var id in new[] { firstCreated, secondCreated, thirdCreated })
        {
            Assert.Equal(RunStatus.Completed, store.GetRun(id).Status);
            Assert.Single(store.GetAttempts(id));
        }
        var policy = new DecisionPolicy(new FixtureDecisionGenerator(), rig.Catalog, new());
        var withTwo = await policy.DecideAsync(TestData.Task(), limited, TestData.Ct);
        var withOne = await policy.DecideAsync(TestData.Task(), newest, TestData.Ct);
        Assert.Equal(Tier.Fast, withTwo.Recommended);
        Assert.Equal(Tier.Balanced, withTwo.Selected);
        Assert.Equal(Tier.Fast, withOne.Selected);
        Assert.Equal("recent-verified-failure:fast:minimum-balanced", Assert.Single(withTwo.Reasons));
    }

    [Fact]
    public async Task ApplicationMinimumStrong_IsNeverDowngradedBySuccessfulHistory()
    {
        using var rig = new TestRig();
        foreach (var route in rig.Catalog.Routes)
            TestData.Seed(rig.Store, rig.Catalog, route, TestData.Success);
        var result = await rig.Execute(TestData.Task(minimum: Tier.Strong));
        Assert.Equal(Tier.Fast, result.Decision.Recommended);
        Assert.Equal(Tier.Strong, result.Decision.Selected);
        Assert.Equal("strong", result.ActualRoute);
        Assert.Contains("application-minimum:strong", result.Decision.Reasons);
        Assert.Equal(0, rig.Fast.Invocations);
        Assert.Equal(0, rig.Balanced.Invocations);
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("unversioned-nonempty")]
    [InlineData("known-version-missing-schema")]
    [InlineData("corrupt-bytes")]
    public void CorruptOrUnsupportedDatabase_IsSurfacedInsteadOfEmptyHistory(string shape)
    {
        using var db = new TestDatabase();
        switch (shape)
        {
            case "unknown-version": db.Sql("PRAGMA user_version=99;"); break;
            case "unversioned-nonempty": db.Sql("CREATE TABLE foreign_table(value TEXT);"); break;
            case "known-version-missing-schema": db.Sql("PRAGMA user_version=1;"); break;
            case "corrupt-bytes": File.WriteAllText(db.Path, "not a SQLite database"); break;
        }
        if (shape is "unknown-version" or "unversioned-nonempty")
            Assert.Throws<InvalidDataException>(() => new SqliteOutcomeStore(db.Path));
        else
            Assert.Throws<SqliteException>(() => new SqliteOutcomeStore(db.Path));
    }

    [Fact]
    public void StoreRead_AfterSchemaCorruptionThrowsRatherThanReturnsEmpty()
    {
        using var rig = new TestRig();
        rig.Database.Sql("DROP TABLE feedback;");
        Assert.Throws<SqliteException>(() => rig.Store.ReadEvidence("sort", rig.Catalog.Revision, new()));
    }
}

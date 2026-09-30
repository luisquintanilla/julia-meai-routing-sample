using System.Text.Json;
using DecisionInference;
using Microsoft.Extensions.AI;

namespace OutcomeRouting.Tests;

public sealed class PolicyAndVerifierTests
{
    [Theory]
    [InlineData(.649999, Tier.Balanced)]
    [InlineData(.65, Tier.Fast)]
    [InlineData(.650001, Tier.Fast)]
    public async Task ConfidenceBoundary_UsesSelectedCandidateProbability(double fast, Tier expected)
    {
        using var rig = new TestRig(generator: new FixtureDecisionGenerator(fast: fast,
            balanced: 1 - fast - .1, strong: .1));
        var result = await rig.Execute();
        Assert.Equal(Tier.Fast, result.Decision.Recommended);
        Assert.Equal(expected, result.Decision.Selected);
        Assert.Equal(fast, result.Decision.Probabilities["fast"]);
        Assert.Equal(expected == Tier.Fast ? "decision-signal" : "low-confidence:balanced",
            Assert.Single(result.Decision.Reasons));
        Assert.Equal(Outcome.Success, result.Feedback.Outcome); // independently verified, not .65 "success chance"
        Assert.Equal(expected == Tier.Fast ? "fast" : "balanced", result.ActualRoute);
    }

    [Fact]
    public async Task NormalizedDistribution_PreservesExactIdsAndProjectionWithoutTruncation()
    {
        using var rig = new TestRig();
        var task = new RoutingTask(new string('c', 64), new string('t', 300), new string('x', 120));
        var decision = await new DecisionPolicy(new FixtureDecisionGenerator(), rig.Catalog, new())
            .DecideAsync(task, [], TestData.Ct);
        Assert.Equal(["balanced", "fast", "strong"], decision.Probabilities.Keys.Order().ToArray());
        Assert.Equal([.9, .08, .02], new[] { decision.Probabilities["fast"],
            decision.Probabilities["balanced"], decision.Probabilities["strong"] });
        Assert.Equal(1, decision.Probabilities.Values.Sum(), precision: 12);
        using var json = JsonDocument.Parse(decision.ProjectedState);
        Assert.Equal(task.Task, json.RootElement.GetProperty("Task").GetString());
        Assert.Equal(task.Context, json.RootElement.GetProperty("Context").GetString());
        Assert.Equal(task.Cohort, json.RootElement.GetProperty("Cohort").GetString());
        var question = Assert.IsType<ChoiceDecisionQuestion>(Assert.Single(DecisionPolicy.Questions));
        Assert.Equal("minimum-tier", question.Id);
        Assert.Equal(["fast", "balanced", "strong"], question.Candidates.Select(c => c.Id));
        Assert.Contains("known outcomes", question.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verified outcomes", question.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN, .08, .02)]
    [InlineData(double.PositiveInfinity, .08, .02)]
    [InlineData(double.NegativeInfinity, .08, .02)]
    [InlineData(-.1, .9, .2)]
    [InlineData(1.1, 0, 0)]
    [InlineData(.6, .2, .1)]
    [InlineData(.9, .1, .1)]
    public async Task InvalidDistribution_IsRejectedBeforeInvocation(double fast, double balanced, double strong)
    {
        using var rig = new TestRig(generator: new FixtureDecisionGenerator(fast: fast, balanced: balanced, strong: strong));
        var task = TestData.Task();
        await Assert.ThrowsAsync<DecisionProtocolException>(() => rig.Execute(task));
        Assert.Equal(0, rig.Fast.Invocations);
        Assert.Empty(rig.Store.GetAttempts(task.RunId));
        Assert.Equal(RunStatus.Failed, rig.Store.GetRun(task.RunId).Status);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData(.9000005, .08, .02)]
    [InlineData(.8999995, .08, .02)]
    public async Task DistributionTolerance_AcceptsWithoutRenormalizing(double fast, double balanced, double strong)
    {
        using var rig = new TestRig(generator: new FixtureDecisionGenerator(fast: fast, balanced: balanced, strong: strong));
        var result = await rig.Execute();
        Assert.Equal(fast, result.Decision.Probabilities["fast"]);
        Assert.Equal("fast", result.ActualRoute);
    }

    [Fact]
    public async Task SelectedNonwinner_IsRejectedEvenWithNormalizedDistribution()
    {
        using var rig = new TestRig(generator: new FixtureDecisionGenerator(Tier.Balanced));
        await Assert.ThrowsAsync<DecisionProtocolException>(() => rig.Execute());
        Assert.Equal(0, rig.Balanced.Invocations);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Theory]
    [InlineData("Fast", "balanced", "strong")]
    [InlineData("fast", "balanced", "extra")]
    [InlineData("fast", "balanced", "balanced")]
    [InlineData("fast", "balanced", "")]
    public async Task CandidateIds_MustBeExactCompleteAndUnique(string first, string second, string third)
    {
        using var generator = new DelegateGenerator((input, ct) =>
        {
            var probabilities = new[] { KeyValuePair.Create(first, .9), KeyValuePair.Create(second, .08),
                KeyValuePair.Create(third, .02) };
            return Task.FromResult(new DecisionResult(input, "fixture", [
                new ChoiceDecisionAnswer(DecisionPolicy.QuestionId, first, probabilities)]));
        });
        using var rig = new TestRig(generator: generator);
        var error = await Record.ExceptionAsync(() => rig.Execute());
        if (third.Length == 0) Assert.IsType<ArgumentException>(error);
        else Assert.IsType<DecisionProtocolException>(error);
        Assert.Equal(0, rig.Fast.Invocations);
        Assert.Equal(0, rig.Router.PendingRequestCount);
    }

    [Fact]
    public async Task FixtureDecisionOptions_RejectUnsupportedOverrides()
    {
        using var generator = new FixtureDecisionGenerator();
        var input = new DecisionInput(JsonSerializer.SerializeToElement(new { }), DecisionPolicy.Questions);
        await Assert.ThrowsAsync<NotSupportedException>(() => generator.GenerateAsync(input,
            new() { ModelId = "override" }, TestData.Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => generator.GenerateAsync(input,
            new() { AdditionalProperties = new() { ["extension"] = 1 } }, TestData.Ct));
    }

    [Fact]
    public void InvalidPolicyAndRouteSettings_AreRejected()
    {
        PolicySettings[] invalidPolicies = [
            new() { ConfidenceFloor = double.NaN }, new() { ConfidenceFloor = -.01 },
            new() { ConfidenceFloor = 1.01 }, new() { ConservativeTier = (Tier)99 },
            new() { EvidenceLimit = 0 }, new() { EvidenceLimit = 7 },
            new() { EvidenceAge = TimeSpan.Zero }, new() { EvidenceAge = TimeSpan.FromDays(31) },
            new() { MaximumAttempts = 0 }, new() { MaximumAttempts = 7 }];
        foreach (var settings in invalidPolicies) Assert.Throws<ArgumentException>(settings.Validate);
        RouteSettings[] invalidRoutes = [
            new(double.NaN), new(-.01), new(2.01), new(MaximumOutputTokens: 0),
            new(MaximumOutputTokens: 4097), new(Reasoning: "unsupported")];
        foreach (var settings in invalidRoutes)
            Assert.Throws<ArgumentException>(() => TestData.Route("fast", Tier.Fast, settings: settings));
    }

    [Theory]
    [InlineData(0, Tier.Fast)]
    [InlineData(1, Tier.Balanced)]
    public async Task PolicySettingEndpoints_AreAcceptedAndApplied(double confidence, Tier expected)
    {
        using var rig = new TestRig();
        var policy = new DecisionPolicy(new FixtureDecisionGenerator(), rig.Catalog,
            new() { ConfidenceFloor = confidence, EvidenceLimit = 6,
                EvidenceAge = TimeSpan.FromDays(30), MaximumAttempts = 6 });
        var route = rig.Catalog.Routes[0];
        var evidence = Enumerable.Repeat(new Evidence(route.Name, route.Identity, route.Tier,
            Outcome.Success, Provenance.Verifier), 6).ToArray();
        var result = await policy.DecideAsync(TestData.Task(), evidence, TestData.Ct);
        Assert.Equal(expected, result.Selected);
        Assert.Equal(Tier.Fast, result.Recommended);
        using var json = JsonDocument.Parse(result.ProjectedState);
        Assert.Equal(6, json.RootElement.GetProperty("Evidence").GetArrayLength());
    }

    [Fact]
    public void TaskBounds_RejectRatherThanTruncate()
    {
        Assert.Throws<ArgumentException>(() => new RoutingTask("sort", new string('x', 301)));
        Assert.Throws<ArgumentException>(() => new RoutingTask("sort", "task", new string('x', 121)));
        Assert.Throws<ArgumentException>(() => new RoutingTask(new string('x', 65), "task"));
        foreach (var cohort in new[] { "", "with space", "café", "key\n" })
            Assert.Throws<ArgumentException>(() => new RoutingTask(cohort, "task"));
        Assert.Throws<ArgumentException>(() => new RoutingTask("sort", " "));
        Assert.Throws<ArgumentException>(() => new RoutingTask("sort", "task", runId: Guid.Empty));
        var nullContext = Assert.Throws<ArgumentNullException>(() => new RoutingTask("sort", "task", context: null!));
        Assert.Equal("context", nullContext.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoutingTask("sort", "task", minimumTier: (Tier)99));
    }

    [Fact]
    public async Task EvidenceBoundsAndIdentity_RejectBeforeGenerator()
    {
        using var rig = new TestRig();
        using var generator = new DelegateGenerator((input, ct) => throw new InvalidOperationException("must-not-call"));
        var policy = new DecisionPolicy(generator, rig.Catalog, new());
        var route = rig.Catalog.Routes[0];
        var valid = new Evidence(route.Name, route.Identity, route.Tier, Outcome.Failure, Provenance.Verifier);
        foreach (var evidence in new IReadOnlyList<Evidence>[] {
            Enumerable.Repeat(valid, 5).ToArray(), [valid with { Outcome = Outcome.Unknown }],
            [valid with { Provenance = Provenance.None }], [valid with { Provenance = (Provenance)99 }],
            [valid with { Outcome = (Outcome)99 }], [valid with { RouteIdentity = "foreign" }],
            [valid with { Tier = Tier.Balanced }], [valid with { Route = "other" }] })
            await Assert.ThrowsAsync<ArgumentException>(() => policy.DecideAsync(TestData.Task(), evidence, TestData.Ct));
        var accepted = await new DecisionPolicy(new FixtureDecisionGenerator(), rig.Catalog, new())
            .DecideAsync(TestData.Task(), Enumerable.Repeat(valid, 4).ToArray(), TestData.Ct);
        using var json = JsonDocument.Parse(accepted.ProjectedState);
        Assert.Equal(4, json.RootElement.GetProperty("Evidence").GetArrayLength());
        Assert.Equal(0, generator.Invocations);
        Assert.Equal(Tier.Balanced, accepted.Selected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void UndefinedRequiredCapabilities_AreRejected(int required)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoutingTask("sort", "task", required: (Capability)required));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestData.Route("fast", Tier.Fast, capabilities: (Capability)required));
    }

    [Fact]
    public async Task RequiredCapabilities_RaiseFloorOrRejectUnavailableRoute()
    {
        var catalog = new RouteCatalog([TestData.Route("fast", Tier.Fast, capabilities: Capability.Text),
            TestData.Route("balanced", Tier.Balanced), TestData.Route("strong", Tier.Strong)]);
        var policy = new DecisionPolicy(new FixtureDecisionGenerator(), catalog, new());
        var result = await policy.DecideAsync(new("sort", "task", required: Capability.Text | Capability.Json), [], TestData.Ct);
        Assert.Equal(Tier.Balanced, result.Selected);
        Assert.Equal("capability-floor:balanced", Assert.Single(result.Reasons));
        var textOnly = new RouteCatalog(Enum.GetValues<Tier>().Select(t =>
            TestData.Route(DecisionPolicy.Id(t), t, capabilities: Capability.Text)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DecisionPolicy(new FixtureDecisionGenerator(), textOnly, new())
                .DecideAsync(new("sort", "task", required: Capability.Json), [], TestData.Ct));
    }

    [Fact]
    public void InvalidCatalogAndFeedback_RejectInconsistentContracts()
    {
        var fast = TestData.Route("fast", Tier.Fast);
        var balanced = TestData.Route("balanced", Tier.Balanced);
        var strong = TestData.Route("strong", Tier.Strong);
        Assert.Throws<ArgumentException>(() => new RouteCatalog([fast, balanced]));
        Assert.Throws<ArgumentException>(() => new RouteCatalog([fast, fast, strong]));
        Assert.Throws<ArgumentException>(() => new RouteCatalog([fast, TestData.Route("other", Tier.Fast), strong]));
        Assert.Throws<ArgumentException>(() => new RouteCatalog([fast, balanced, strong, null!]));
        Assert.Throws<ArgumentException>(() => new RouteCatalog(
            Enumerable.Range(0, 7).Select(i => TestData.Route($"r{i}", (Tier)(i % 3)))));
        Assert.Throws<ArgumentException>(() => new Feedback(Outcome.Success, Provenance.None, "source"));
        Assert.Throws<ArgumentException>(() => new Feedback(Outcome.Unknown, Provenance.Verifier, "source"));
        Assert.Throws<ArgumentException>(() => new Feedback((Outcome)99, Provenance.Verifier, "source"));
        Assert.Throws<ArgumentException>(() => new Feedback(Outcome.Success, Provenance.Verifier, "bad source"));
    }

    [Theory]
    [InlineData("[1,2,3]", Outcome.Success)]
    [InlineData("[3,1,2]", Outcome.Failure)]
    [InlineData("nonempty is not success", Outcome.Failure)]
    [InlineData("{", Outcome.Failure)]
    [InlineData("{\"values\":[1,2,3]}", Outcome.Failure)]
    [InlineData("[1,\"2\",3]", Outcome.Failure)]
    [InlineData("[1,null,3]", Outcome.Failure)]
    [InlineData("[1,true,3]", Outcome.Failure)]
    [InlineData("[1,2.5,3]", Outcome.Failure)]
    [InlineData("[1,2,2147483648]", Outcome.Failure)]
    [InlineData("[1,2]", Outcome.Failure)]
    [InlineData("[1,2,3,4]", Outcome.Failure)]
    [InlineData("", Outcome.Failure)]
    public void SortVerifier_ChecksIndependentExpectedValuesAndJsonTypes(string text, Outcome expected)
    {
        var feedback = TestData.Sort.Verify(new(new ChatMessage(ChatRole.Assistant, text)));
        Assert.Equal(expected, feedback.Outcome);
        Assert.Equal(Provenance.Verifier, feedback.Provenance);
        Assert.Equal("exact-sort-v1", feedback.Source);
    }

    [Fact]
    public void SortVerifier_SortsIndependentInputIncludingNegativesAndDuplicates()
    {
        int[] input = [3, -2, 3, 0];
        var verifier = new ExactSortVerifier(input);
        input[0] = 99; // expectations are snapshotted from input, not inferred from response
        Assert.Equal(TestData.Success, verifier.Verify(new(new ChatMessage(ChatRole.Assistant, "[-2,0,3,3]"))));
        Assert.Equal(TestData.Failure, verifier.Verify(new(new ChatMessage(ChatRole.Assistant, "[-2,0,3,99]"))));
        Assert.Equal(TestData.Failure, verifier.Verify(new(new ChatMessage(ChatRole.Assistant, "[-2,0,3]"))));
    }

    [Fact]
    public void SortVerifier_UnsupportedShapesAreUnknownNotSuccess()
    {
        ChatResponse[] unsupported = [
            new(Array.Empty<ChatMessage>()),
            new(new ChatMessage(ChatRole.User, "[1,2,3]")),
            new(new[] { new ChatMessage(ChatRole.Assistant, "[1,2,3]"), new ChatMessage(ChatRole.Assistant, "") }),
            new(new ChatMessage(ChatRole.Assistant, [new TextContent("[1,2,3]"), new DataContent(new byte[] { 1 }, "image/png")]))];
        foreach (var response in unsupported)
            Assert.Equal(Feedback.Unknown("unsupported-response"), TestData.Sort.Verify(response));
    }
}

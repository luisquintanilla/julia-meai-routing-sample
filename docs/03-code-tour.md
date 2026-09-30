# 3. From task to evidence: a code tour

[Learning path](README.md) | Previous: [offline walkthrough](02-offline-walkthrough.md) |
Next: [native Julia](04-native-julia.md)

Follow the sorting task through the actual implementation. The C# excerpts below
are taken from the linked source files; they are **not standalone programs**.
Run the console project rather than pasting a fragment without its containing
types and dependencies.

## Find your starting point

| File | Responsibility |
|---|---|
| [`Program.cs`](../src/JuliaRouting.Sample/Program.cs) | CLI, fixture demo, optional native/live setup |
| [`Contracts.cs`](../src/OutcomeRouting/Contracts.cs) | Typed tasks, tiers, routes, options, outcomes, identity |
| [`DecisionPolicy.cs`](../src/OutcomeRouting/DecisionPolicy.cs) | Compact decision state, typed question, deterministic selection rules |
| [`OutcomeRouter.cs`](../src/OutcomeRouting/OutcomeRouter.cs) | Real MEAI failover selection and actual attempt recording |
| [`OutcomeApplication.cs`](../src/OutcomeRouting/OutcomeApplication.cs) | Execute, independently verify, and finish the durable run |
| [`SqliteOutcomeStore.cs`](../src/OutcomeRouting/SqliteOutcomeStore.cs) | Transactional runs, attempts, feedback, comparable evidence |
| [`Fixtures.cs`](../src/OutcomeRouting/Fixtures.cs) | Explicitly simulated decision generator and chat clients |
| [`JuliaDecisionGenerator.cs`](../vendor/src/DecisionInference.Julia/JuliaDecisionGenerator.cs) | Resident native Julia prepare/score/decode composition |

The console project references the core and vendored Julia provider. The core
depends on `IDecisionGenerator`, not a specific native model. The native CPU ORT
package belongs in the executable; vendored libraries keep their original
Managed ORT references.

## 1. Describe the application task

`Demonstrations.Task` in `Program.cs` returns a typed request:

```csharp
internal static RoutingTask Task(string cohort) => new(cohort,
    "Sort the integers [3,1,2] ascending. Return only the JSON array, with no prose.",
    required: Capability.Text | Capability.Json);
```

`RoutingTask` carries a new run ID, an application-owned cohort, short task/context,
required capabilities, and an optional minimum tier. Capabilities are declarations
you own, not facts inferred from model names.

`ClientSet.Fixtures` maps each stable tier to a uniquely named `ChatRoute` with a
concrete fixture model/configuration. `LiveConfiguration.CreateClients` performs
the analogous mapping to the standard OpenAI MEAI adapter when explicitly enabled.

## 2. Ask a typed decision question

`DecisionPolicy` defines the exact question and alternatives:

```csharp
public static IReadOnlyList<DecisionQuestion> Questions { get; } = Array.AsReadOnly<DecisionQuestion>(
[
    new ChoiceDecisionQuestion(QuestionId,
        "Choose the minimum sufficient chat capability for this task, considering relevant known outcomes.",
        [new("fast", "Routine, clear, short transformations"),
         new("balanced", "Multi-step work or routine work with recent failures"),
         new("strong", "Complex reasoning, high risk, or failures at balanced capability")])
]);
```

The IDs are exact keys; the descriptions define meaning. The state includes the
task, cohort, required/minimum capability, current route summaries, and bounded
relevant evidence. `RoutingJson` supplies generated JSON metadata:

```csharp
var result = await generator.GenerateAsync(state, Questions, RoutingJson.Default.DecisionState,
    cancellationToken: cancellationToken);
var answer = result.GetAnswer<ChoiceDecisionAnswer>(QuestionId);
```

The real Julia provider returns a typed `ChoiceDecisionAnswer`, not generated
prose parsed into a label. `SelectedCandidateId` and the `Probabilities` keyed by
exact IDs are validated before policy executes. A malformed distribution or
nonwinning selected candidate is an error, not a reason to invent a default.

The fixture implements the **same** `IDecisionGenerator` seam but visibly returns
model ID `fixture-not-julia`. Do not use its outputs as native accuracy data.

## 3. Apply application rules

`DecideAsync` starts with the recommendation, applies the confidence policy,
enforces the application minimum, raises the floor for relevant known failures,
then checks declared capabilities. `DecisionSnapshot` keeps both
`Recommended` and `Selected`, alongside `Probabilities`, `Reasons`, and the
caller-visible `ProjectedState`.

`ProjectedState` is passed to the decision model and printed in native mode, but
is deliberately excluded from persisted decision records. It can contain task
text, so treat console captures separately from database privacy.

The [policy reference](06-policy-and-lifecycle.md#policy-order-and-knobs) explains
the exact order and illustrative thresholds. They are application settings, not
properties learned by Julia.

## 4. Let real MEAI execute and fail over

`OutcomeRouter : FailoverChatClient` overrides MEAI's `SelectClientAsync` and
`OnRoutingUpdateAsync`. `FailoverChatClient` itself derives from
`RoutingChatClient` and owns the invocation/retry/stream-commit loop.
This sample does not reimplement that loop.

Once the policy decision is recorded, the selection excerpt is:

```csharp
var route = _catalog.Routes.Where(r => r.Tier >= session.Decision.Selected &&
    (r.Capabilities & session.Task.Required) == session.Task.Required && !session.Invoked.Contains(r.ExecutionIdentity))
    .OrderBy(r => r.Tier).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault()
    ?? throw new InvalidOperationException("No untried eligible route remains.");
session.Invoked.Add(route.ExecutionIdentity);
session.Current = route;
return route.Client;
```

The selector returns an `IChatClient`; MEAI calls it. It never picks a route below
the policy floor. Physical endpoint/model/options aliases are not retried under
different metadata labels. The attempt limit bounds total route invocations.

Each route wraps its client using actual MEAI `ConfigureOptions`. Route-specific
model/settings are applied to cloned options, not the caller's object. Internal
request metadata is removed before provider forwarding.

The hook records the actual invoked client, exception type, timing,
`ResponseCompleted`, `OutputCommitted`, and time to first update. A completed
attempt identifies `session.Completed`. **The hook cannot inspect or grade the
response**, and it never records task Success just because transport completed.

MEAI's experimental API suppression is limited to this implementation file:
`#pragma warning disable MEAI001` / `restore`. It is not hidden solution-wide.
Selection/hook exceptions terminate without another cleanup callback; the router
removes its per-request state before rethrowing.

## 5. Verify after completion, using the actual route

`OutcomeApplication.ExecuteAsync` begins a durable run, calls the router, and
checks the result only after response completion. The verifier receives the
response, not the initial recommendation.

`ExactSortVerifier` derives its expected array from the supplied input:

```csharp
private readonly int[] _expected;
public ExactSortVerifier(IEnumerable<int> input) => _expected = input.Order().ToArray();
```

It requires one supported assistant text response, parses a JSON integer array,
and compares length, values, multiplicity, and order against `_expected`.
Malformed supported text is Failure; unsupported response shapes are Unknown.
This is an independent check, not a self-judge.

The application persists terminal feedback on the actual completed route:

```csharp
store.Finish(task.RunId, RunStatus.Completed, actual.Identity, verifier is null ? null : feedback);
return new(task.RunId, response, session.Decision!, session.Attempts.AsReadOnly(), actual.Name, feedback);
```

Notice that `RunStatus.Completed` and `feedback.Outcome` are different concepts.
A run can be completed with Failure or Unknown. A poor response ends this request;
it does not become a fake transport exception to force a retry.

## 6. Read evidence on a later request

`SqliteOutcomeStore.ReadEvidence` selects only completed, current-catalog,
same-cohort, recent, known verifier/application outcomes correlated to a stored
completed actual attempt. It bounds their number and returns short summaries,
not raw prompts or output. Fresh store instances see committed feedback.

An omitted verifier stores no feedback row. An application with an independently
known result can use `GetRun` and `ReportFeedback`; see
[feedback semantics](06-policy-and-lifecycle.md#feedback-and-storage).
Explicit terminal Unknown is different: it cannot later be overwritten.

## Read the tests as executable explanations

[`RoutingAcceptanceTests.cs`](../tests/OutcomeRouting.Tests/RoutingAcceptanceTests.cs)
contains, among others:

| Question | Test |
|---|---|
| Does the alternate receive the outcome? | `PreOutputFailover_RecordsEachAttemptAndAttributesFailureToBalancedAlternate` |
| Can policy mutate caller options? | `ConfigureOptions_ClonesPreservesCallerAndStripsRequestMetadataDownstream` |
| Is partial streamed output a success? | `StreamingEarlyDispose_IsAbandonedAndCleansPendingWithoutPositiveFeedback` |
| Can failover continue after output? | `StreamingPostFirstUpdateFailure_NeverInvokesAlternateOrVerifier` |

[`OutcomeStoreTests.cs`](../tests/OutcomeRouting.Tests/OutcomeStoreTests.cs) covers
reload, feedback uniqueness, concurrent writers, configuration isolation and age
windows. [`PolicyAndVerifierTests.cs`](../tests/OutcomeRouting.Tests/PolicyAndVerifierTests.cs)
covers probability handling, exact candidates, bounds and independent verification.

Read these before adapting the sample to real tasks. The typed seam is reusable;
the verifier, cohort definition, capabilities, model mapping and risk policy remain
application responsibilities.

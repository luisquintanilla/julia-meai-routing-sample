# 6. Policy, evidence, and lifecycle reference

[Learning path](README.md) | Previous: [live chat](05-live-chat.md) |
[Troubleshooting](troubleshooting.md)

This page describes the **implemented sample rules**, not a production routing
recommendation or an empirically optimized policy.

## Policy order and knobs

[`DecisionPolicy.DecideAsync`](../src/OutcomeRouting/DecisionPolicy.cs) applies these
steps in order:

1. Validate the exact complete `fast` / `balanced` / `strong` candidate distribution
   and winning selected candidate.
2. Start from that recommendation. If its selected probability is **below** the
   confidence floor, replace it with the configured conservative tier.
3. Enforce the task's application minimum tier.
4. For every relevant known Failure, require at least one tier above the actual
   failed tier; Strong remains Strong.
5. Require declared task capabilities. Raise to a capable tier if necessary;
   if no route qualifies, fail explicitly.

The conservative fallback is a configured policy choice, not guaranteed safer
model behavior. If you configure it lower than a recommendation, step 2 can choose
that lower tier. Application minimums and the failure floor still apply.
Protect high-risk tasks with explicit minimums and appropriate checks; don't
interpret this sample's default confidence threshold as measured success risk.

| Setting | Default | Supported input / effect |
|---|---|---|
| `ConfidenceFloor` | `0.65` | Finite `0..1`; selected probability below it triggers fallback |
| `ConservativeTier` | `Balanced` | Fast, Balanced or Strong |
| `EvidenceLimit` | `4` | `1..6` eligible known outcomes |
| `EvidenceAge` | 7 days | Positive, at most 30 days; CLI accepts integer days |
| `MaximumAttempts` | `3` | `1..6` total configured route invocations |
| `RoutingTask.MinimumTier` | `Fast` | Application API floor; not a CLI flag |
| `RoutingTask.Required` | `Text` | Declared Text and/or Json; the console sorting task requires both |

From the repository root, with prepared native assets:

```powershell
dotnet run --project src\JuliaRouting.Sample -- --julia artifacts\decision-models\julia `
  --confidence-floor 0.75 --conservative-tier strong `
  --history-limit 4 --history-days 7 --max-attempts 2
```

Flags modify native/live mode. The offline demo deliberately uses fixed settings
for reproducible assertions. There is no JSON configuration file or route cache.
Use `--help` for the actual parser-supported options.

## A deliberately small outcome rule

A known Failure at a comparable Fast configuration raises the future minimum to
Balanced; at Balanced, to Strong; at Strong, it keeps Strong. Both
Verifier- and explicitly Application-reported outcomes are eligible.
The failure reason preserves which source reported it.

Successes are available in projected history, but do not deterministically
erase failures or lower an application's minimum. Unknown is not passed as
known evidence. A failure stops enforcing its floor only when it ages out or
enough newer eligible outcomes move it outside the bounded window.

The store uses completion time, with run sequence as a tie-breaker, and includes
the exact age cutoff. Newest unknown/ineligible records do not consume the
eligible-evidence limit. Late application feedback does not reset the run's age.

This is intentionally blunt. It assumes the application chose an appropriately
comparable task cohort. There is no similarity model, calibrated success forecast,
automatic downgrade policy, exploration, weight update, or guarantee of optimal
cost/quality.

## Comparable means cohort and concrete configuration

The cohort is a bounded application key, such as `sort-three-v1`, **not an
automatically inferred semantic category**. Change it when task/check semantics
change. Do not group unrelated tasks simply because they share a tier. Use
synthetic, nonidentifying keys, not names, customer IDs, private project names,
or raw task text.

Two identities serve different purposes:

| Identity | Inputs | Purpose |
|---|---|---|
| Public `ChatRoute.Identity` | Route name, tier, capabilities, endpoint, model, config revision, explicit options | Attribute feedback to an exact declared route configuration |
| Internal `ExecutionIdentity` | Endpoint, model, explicit options | Avoid retrying the same physical call under a different alias/revision label |

`RouteCatalog.Revision` fingerprints sorted public route identities. Evidence
must match **both cohort and the entire current catalog revision**, not merely
"the Fast tier." Reordering routes does not change revision; replacing a model,
endpoint, reasoning effort, temperature, output budget, name, tier, capabilities,
or config revision does.

Catalog-wide isolation is conservative: even an unrelated catalog change excludes
old evidence. That is preferable here to silently assigning a replaced model's
feedback to its successor. Change the explicit config revision for other provider
behavior changes. Hashing identity is not a substitute for sanitizing identifying
or sensitive metadata.

## Feedback and storage

The application-facing [IChatClient](../src/OutcomeRouting/OutcomeRoutingChatClient.cs)
constructs the core wiring described here. It borrows caller-supplied generators
and clients, including shared clients, and never disposes them. Dispose the routed client
first, after stopping operations, then dispose dependencies at their owning scope.

[`Feedback`](../src/OutcomeRouting/Contracts.cs) has a task outcome, provenance,
and a short source key. These combinations are valid:

| Outcome | Provenance | Meaning |
|---|---|---|
| Success or Failure | Verifier | Independently defined application checks produced a known result |
| Success or Failure | Application | The application explicitly reports an independently known result |
| Unknown | None | Quality is not known; it cannot become positive evidence |

The store correlates feedback to a stored local run ID and its **actual completed
route**, backed by a completed attempt. Unknown/foreign IDs, noncompleted runs,
wrong-route feedback, invalid combinations and conflicts are rejected. Repeating
identical terminal feedback returns `false` and does not add another evidence row.
The first valid explicit application report returns `true`.

An omitted verifier leaves a completed run with **no feedback row**, so quality
remains unknown and later application feedback is possible. Explicit Unknown from
a verifier is terminal and immutable. It cannot be overwritten by Success.

The following integration example uses existing APIs; `store` and `runId` must
refer to a local completed run whose verifier was omitted:

```csharp
RunRecord run = store.GetRun(runId);
if (run.Status != RunStatus.Completed || run.ActualRouteIdentity is null)
{
    throw new InvalidOperationException("Independent feedback requires a completed actual route.");
}

store.ReportFeedback(
    run.RunId,
    run.ActualRouteIdentity,
    new Feedback(
        Outcome.Failure,
        Provenance.Application,
        "application-check-v1"));
```

The optional `OutcomeRoutingChatClient.ReportFeedback(runId, feedback)` method finds the same
completed actual route internally. Only use either API after an independent check;
it is not a way to mark transport completion successful.

Do not report Failure merely because a request timed out: that is transport
telemetry, not a completed task-quality result. Do not invent Success because a
response is nonempty.

[`SqliteOutcomeStore`](../src/OutcomeRouting/SqliteOutcomeStore.cs) uses schema
version 1, foreign keys, uniqueness/check constraints, short-lived connections,
and transactional terminal writes. SQLite serializes concurrent writers;
operations have a bounded busy timeout rather than success-shaped fallbacks.
Fresh and already-open store instances read updated committed evidence.
Schema/integrity, corruption, uniqueness, permission and storage errors surface.

Raw task/context, output, credential fields and arbitrary feedback reason text
are not persisted by default. Recorded cohort/source/route keys must still be
safe metadata. There is no automatic history purge, encryption, distributed sync,
or recovery operation. A user-owned local DB may grow beyond the bounded
**selection** window; evidence limits are not disk-retention limits.

## Run completion and task quality are separate

| Run status | Typical cause | Eligible known quality evidence? |
|---|---|---|
| Running | Begun, not yet terminal; may remain after a process crash | No |
| Completed | A response completed on an actual route | Only with valid known verifier/application feedback |
| Failed | Provider/selection/hook/application execution error | No |
| Cancelled | Request cancellation | No |
| Abandoned | Started streaming enumeration disposed before completion | No |

A completed run can have Success, Failure or Unknown quality. A verifier that
throws (or improperly returns null) surfaces an error while preserving completed
transport with no fabricated task outcome. Later independent application feedback
is still possible. Storage failure itself may prevent recording terminal state;
there is no claim of durability after an unsuccessful write.

There is no automatic recovery that turns stale Running into Completed.
Such a run is excluded from evidence and cannot receive feedback.

## Transport failover and streaming

MEAI's `FailoverChatClient` supplies the actual retry loop. Selection occurs before
each invocation, and attempts are reported separately. An eligible alternate may
be chosen after an **uncancelled pre-output provider failure**, subject to the
attempt cap and remaining routes.

MEAI's hook exposes the invoked client, exception, active duration, completion,
output commitment, and first-update time. It contains neither the chat response
nor independent quality feedback. The application verifier operates later.

Any update delivered to a streaming caller commits output, including an update
with no text. A later exception is terminal: **no mid-stream failover**.
Cancellation stops reselection. Poor successful output does not trigger built-in
failover; same-request quality cascading/hedging/ensembles are out of scope.

`OutcomeApplication.StreamAsync` accumulates only fully completed supported text
streams for verification, with a **16,384-character** accumulation bound.
Unsupported content or non-assistant/non-null roles make quality Unknown.
Empty completed streams are explicit Unknown. Partial disposed output never
becomes Success. Use `await foreach` or `await using` so enumerators are disposed;
leaking an active enumerator prevents terminal callbacks and cleanup.

The sample rejects provider conversation IDs, continuation tokens,
`AllowBackgroundResponses=true`, nonempty tools, any tool mode or
`AllowMultipleToolCalls` setting, and response-format options without a declared
Json task capability. Empty tools and explicit background `false` remain allowed.
It is an independent stateless text/JSON pipeline, not an agent loop.

## Native ownership and experimental APIs

The application owns the generator and chat route clients; the router does not
dispose those dependencies itself. Reuse resident native inference and dispose
on shutdown, not during in-flight work. Julia's serialized synchronous
`GenerateAsync` implementation and ORT cooperative cancellation have the
[native lifetime caveats](04-native-julia.md#lifetime-and-asynchronous-caveats).

The routing primitives appeared in MEAI 10.9.0 and are `[Experimental]` with
**MEAI001** in the pinned 10.10.0 package. Only `OutcomeRouter.cs` suppresses that
diagnostic. The sample uses a real `FailoverChatClient` (therefore a
`RoutingChatClient`), not a wrapper named after those APIs.
`OrderedFailoverChatClient` is useful for fixed-order fallback; this sample uses
the custom selector to enforce its policy and exact-route attribution.

Primary references:
[MEAI routing article](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/),
[attempt-hook contract](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.failoverchatclient.onroutingupdateasync?view=net-11.0-pp),
[Jevia outcome-loop architecture](https://github.com/assistant-ui/jevia/blob/main/docs/architecture.md).

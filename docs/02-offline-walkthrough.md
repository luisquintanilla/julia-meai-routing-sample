# 2. Follow one task through the offline demo

[Learning path](README.md) | Previous: [concepts](01-concepts.md) |
Next: [code tour](03-code-tour.md)

From the repository root, after the [quickstart restore](../README.md#try-it-first):

```powershell
dotnet run --project src\JuliaRouting.Sample --no-restore
```

**Both decision signals and downstream responses are fixtures.** No Julia weights,
credentials, or chat endpoint are needed. Real MEAI handles execution, real
SQLite persists evidence, and an independent deterministic verifier checks results.

## The fixed task and check

The application asks:

```text
Sort the integers [3,1,2] ascending. Return only the JSON array, with no prose.
```

The verifier separately sorts those input integers to obtain `[1,2,3]`.
The Fast fixture intentionally returns `[3,1,2]`; the Balanced/Strong fixtures
return `[1,2,3]`. This makes the policy transitions reproducible, **not a benchmark
of real models or tiers**.

The demo uses separate cohort keys for verified-history, unknown-history, and
provider-health scenarios. Different task families must not accidentally share
evidence. The failover case also has a different concrete fixture configuration.

## Captured output

The following is captured from the actual deterministic run. Only the last
machine-specific database path is omitted; the app prints it for local inspection.

```text
OFFLINE DEMO: BOTH decision signals and downstream responses are SIMULATED.
Real MEAI FailoverChatClient + real SQLite + independent exact-sort verifier.
1 initial: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Failure provenance=Verifier
  reasons=decision-signal; attempts=fast:completed
2 after persisted failure/reload: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=balanced actual=balanced outcome=Success provenance=Verifier
  reasons=recent-verified-failure:fast:minimum-balanced; attempts=balanced:completed
3 explicit unknown: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Unknown provenance=None
  reasons=decision-signal; attempts=fast:completed
4 after unknown: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Unknown provenance=None
  reasons=decision-signal; attempts=fast:completed
5 pre-output failover: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=balanced outcome=Success provenance=Verifier
  reasons=decision-signal; attempts=fast:provider-failed -> balanced:completed
All offline transition, persistence, unknown and actual-route attribution assertions passed.
```

| Column | What it tells you |
|---|---|
| `signal` / `distribution` | Original fixture recommendation and full relative candidate values |
| `policy` | Minimum tier selected after application rules |
| `actual` | Route that really completed, possibly different after failover |
| `outcome` / `provenance` | Task quality and the independent source of that result |
| `reasons` | Why policy accepted or overrode the original signal |
| `attempts` | Transport sequence; `completed` does **not** mean correct |

## Read the five transitions

**1. Initial request:** Fast is recommended and allowed. Its invocation completes
normally, but the response is incorrectly sorted. The verifier records Failure
for the actual Fast configuration. There is one attempt, not a hidden retry.

**2. A later request after reload:** a fresh store instance reads the committed
failure for the same cohort/configuration. The fixture still recommends Fast.
Policy raises the minimum to Balanced because of that failure. Balanced completes,
and its independently checked answer is Success. Persistence affects behavior,
not just display text.

**3. Explicit unknown:** in a separate cohort, a verifier says it cannot determine
quality. The store records terminal Unknown/None. Completed transport is not
converted to successful task evidence.

**4. After unknown:** a later request in that cohort still selects Fast. It has
no verifier, so its completed response is also Unknown. Unlike explicit terminal
Unknown, omitted verification leaves room for later explicit application feedback.
Neither state supplies eligible evidence by itself.

**5. Pre-output provider failure:** a separate fixture throws a simulated provider
exception before returning output. The real MEAI failover loop selects Balanced.
There are two attempt records. The verifier checks the completed alternate, and
Success belongs to **Balanced**, never to the failed Fast route.

## Inspect behavior, not just exit status

[`Demonstrations.RunAsync`](../src/JuliaRouting.Sample/Program.cs) contains assertions
for each transition, actual-attempt attribution, stored evidence, and cleanup.
Each demo run creates a unique `.routing\demo-<unique>\history.db`; reruns cannot
inherit the previous demo's failures. Default demonstrations do not mix data into
native/live history, and `--store` is deliberately rejected without `--julia`.

The database stores three kinds of record: the run, each actual attempt, and
terminal feedback. It does not store raw task/output text. Do not upload your local
database or terminal transcript when adapting the app to real data.

## What the example does not prove

The exact-sort check validates this small transformation only: values, count,
order, and supported response shape. It cannot certify a coding fix, legal answer,
summary, or every requirement of a real task. Those applications need their own
independently defined checks or trustworthy explicit feedback.

No quality failure is disguised as a transport exception. No same-request quality
cascade or automatic downgrade is implemented. Unknown does not mean success,
and collecting outcomes does not train Julia.

**Try a prediction:** would a verified Failure at the completed Balanced alternate
make a later comparable request require Strong? Yes; the same recent-failure rule
raises the minimum one tier. That case is exercised by
`PreOutputFailover_RecordsEachAttemptAndAttributesFailureToBalancedAlternate`
in the [acceptance tests](../tests/OutcomeRouting.Tests/RoutingAcceptanceTests.cs).

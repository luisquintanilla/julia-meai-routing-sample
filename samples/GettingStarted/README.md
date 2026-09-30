# The application API, without model setup

**Read [Program.cs](Program.cs).** The application creates clients, maps tiers,
creates an `IChatClient`, and makes two normal MEAI `GetResponseAsync` calls.
The decision generator, physical clients, route mapping, `IOutcomeStore`, policy,
independent verifier selector, and `IRoutingObserver` are visible reusable components.
No catalog/request-session coordinator needs wiring. `OutcomeRoutingChatClient` and
`ChatRoute.Create` belong to this sample; the invocation contract is standard MEAI.

From the repository root after restore:

```powershell
dotnet run --project samples\GettingStarted --no-restore
```

```text
SIMULATED decisions and chat responses. Real routing, verification and persistence.
[run-1] decision: recommended=fast probabilities=[fast:0.9, balanced:0.08, strong:0.02] policy=fast route=fast
[run-1] attempt 1: actual=fast completed=True committed=False
[3,1,2]
[run-2] decision: recommended=fast probabilities=[fast:0.9, balanced:0.08, strong:0.02] policy=balanced route=balanced
[run-2] attempt 1: actual=balanced completed=True committed=False
[1,2,3]
```

The actual generated run IDs replace the shorthand above. A selection record
arrives before each provider invocation; an attempt record identifies the actual
invocation before the standard answer is printed. This is not a canned log:
the [console observer](../Shared/ConsoleRoutingObserver.cs) formats the shared
library's immutable `RoutingObservation`. The tested
[recording observer](../Shared/RecordingRoutingObserver.cs) captures those same
objects without output or persistence. Omit `Observer` for silence; observation
does not imply task success.

Both the decision signal and chat output are fixtures. The fast demonstration
client deliberately returns an unsorted array; the independent verifier catches
it. A later request sees the persisted failure, even though the fixture still
recommends Fast. This says nothing about real model quality.

`DemoChatClient` is [small mechanical support](../Shared/DemoChatClient.cs), not a
hidden routing selector. It implements `IChatClient` and advertises its model and
endpoint through MEAI metadata. The routed client borrows clients and the decision
generator; the caller disposes them after it.

[SortVerification.ForMessages](../Shared/SortVerification.cs) independently sorts
the known input only when the request matches it. It never grades an unrelated
prompt. Verification is configured at creation, not an extra per-call API.

**Try one change:** make the fast response `[1,2,3]`. The verified first success
does not impose a higher tier on the next request. Removing the verifier makes
outcomes Unknown, never automatically successful. The second response then stays
on Fast. Optional [metadata/feedback and streaming](../../docs/03-code-tour.md#optional-outcome-information)
do not complicate the normal request/response entrypoint.

Next, use [the same application with real Julia](../JuliaRouting/README.md), or
try [real Ollama answers](../OllamaRouting/README.md). The
[API and internals tour](../../docs/03-code-tour.md) distinguishes decision,
history, verifier and observation contracts from the standard MEAI invocation.

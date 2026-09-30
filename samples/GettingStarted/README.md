# The application API, without model setup

**Read [Program.cs](Program.cs).** The application creates clients, maps tiers,
creates an `IChatClient`, and makes two normal MEAI `GetResponseAsync` calls.
No catalog/router/store coordinator needs wiring. `OutcomeRoutingChatClient` and
`ChatRoute.Create` belong to this sample; the invocation contract is standard MEAI.

From the repository root after restore:

```powershell
dotnet run --project samples\GettingStarted --no-restore
```

```text
SIMULATED decisions and chat responses. Real routing, verification and persistence.
[3,1,2]
[1,2,3]
```

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
read the [API and internals tour](../../docs/03-code-tour.md).

# Application API first; internals when you need them

[Sample home](../README.md) | [References](README.md)

## Start with the caller

Read the complete [offline application](../samples/GettingStarted/Program.cs) or
[Julia application](../samples/JuliaRouting/Program.cs). They visibly create the
decision generator and chat clients, map three tiers, create an `IChatClient`, and
call standard MEAI `GetResponseAsync`. `OutcomeRoutingChatClient` is a
**sample-owned IChatClient implementation**, not a released MEAI type.

Its constructor accepts caller-owned `IDecisionGenerator` and `ChatRoute` mappings
of physical `IChatClient` instances, with optional
`OutcomeRoutingOptions`. Those group history path, comparable-task cohort, policy,
capabilities/minimum tier, an independent request-specific verifier factory and
`IRoutingObserver`. The examples now declare those components visibly rather than
presenting a convenience facade as the only way to compose them.
The defaults keep quality Unknown; response completion is never success evidence.
The SQLite store uses short-lived connections, not a connection you manage.

`History` accepts the existing `IOutcomeStore` abstraction, such as
`SqliteOutcomeStore`; the library borrows that instance. Alternatively, `HistoryPath`
creates SQLite history internally. Setting both is rejected, not silently ignored.
With neither, the existing `.routing\history.db` default applies. The history
abstraction has no disposal requirement; each SQLite operation owns its short-lived
connection. Custom implementations own their resources and must support concurrency.

Dispose the routed client **before** its borrowed generator and route clients.
Shared clients are disposed only once at their owning scope; this implementation
never disposes them. Stop operations and dispose stream enumerators before disposing
their dependencies. Wrapper disposal is idempotent and rejects new work.

### Complete messages; bounded decision projection

The client snapshots the supplied sequence once, including single-use enumerables,
and forwards the original `ChatMessage` objects, roles, author names, text fragments
and whitespace. The provider's actual `ChatResponse` is returned with messages,
usage, model/response IDs, finish reason, raw representation and continuation
properties intact. Returned continuation properties are not accepted as new
request options in this stateless sample.

Julia sees a separate bounded projection: a single user message's complete text,
or the complete text of **every** message prefixed with its role and joined by newlines.
That projection must fit **300 characters** including role prefixes and separators.
Overflow is an explicit error before a run is persisted or a model is invoked,
not permission to drop earlier turns or truncate text. This is the sample's
policy bound, **not a MEAI bound or a Julia token limit**. Ordinary short
system/user/assistant history works; no provider conversation state is assumed.

Only text is supported. Images, audio, tool/custom roles and non-text content,
provider continuation/conversation options, background responses and tool controls
fail explicitly. A requested response format requires declared Json capability.
Caller options are cloned, and the internal routing session key never reaches
the provider. Route-specific options still override model/temperature/output budget.

## Optional structured observation

[`IRoutingObserver`](../src/OutcomeRouting/RoutingObservation.cs) is a shared
library contract, not an MEAI middleware or an application execution API. Its
`ObserveAsync(RoutingObservation, CancellationToken)` method receives immutable
run-correlated metadata:

| Phase | When | What the route means |
|---|---|---|
| `RouteSelected` | After decision persistence and policy selection, before provider invocation | The route about to be invoked; not a completed answer |
| `AttemptFinished` | After the actual attempt is persisted, before verification/reselection | The actual invoked route and its completion/commit/error telemetry |

Both phases include decision model, recommendation, copied read-only candidate
probabilities/reasons, policy tier, route name/identity/tier and the same local
`RunId`. Attempt notifications also include the immutable `AttemptRecord`.
They omit raw messages, projected state, response and feedback. Model/route metadata
can still identify infrastructure: choose safe names and control logs yourself.
Telemetry is not task-quality evidence. Use independent verification or application
feedback for that.

The default is **no observer and no output**. Shared sample
[`ConsoleRoutingObserver`](../samples/Shared/ConsoleRoutingObserver.cs) formats
one atomic correlated line; [`RecordingRoutingObserver`](../samples/Shared/RecordingRoutingObserver.cs)
uses a concurrent queue and returns read-only in-memory snapshots. They are
sample components, not extra library front doors. The collector is deliberately
not persisted or globally registered; its owning application controls lifetime
and retention. A production observer should apply an appropriate retention bound.

Callbacks are awaited, ordered within each request, and may overlap across
concurrent requests. Implementations must be thread-safe and promptly complete;
there is no fire-and-forget task, `AsyncLocal`, observer timeout or error-swallowing
queue. Selection notifications use the request cancellation token and cancellation
is checked again before invocation. Attempt notifications use `CancellationToken.None`
so cancellation/abandonment cannot suppress already-persisted cleanup telemetry.

Observer exceptions propagate. MEAI selection/hook failures terminate the request,
**not** fail over to another model. The coordinator records Failed, or Cancelled
when the request token is cancelled, with no fabricated feedback. An attempt
observer can therefore fail after provider output has completed or been delivered;
its persisted attempt remains, but no successful task result is invented.
Invalid input, failed decision/persistence, or cancellation before selection can
produce no notification. There is no promised terminal observer event: durable
run status and independent outcomes remain history responsibilities.

Observers are borrowed and not disposed by the routing client. In the standard
response path they run before `ChatResponse` is returned; in streaming they are a
separate side effect and never become synthetic `ChatResponseUpdate` objects.

## Standard MEAI composition and streaming

For example, an existing caller-owned routed client can use real option middleware:

```csharp
using IChatClient configured = chatClient.AsBuilder()
    .ConfigureOptions(options => options.TopP = 0.25f)
    .Build();

ChatResponse response = await configured.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(response.Text);
```

This does not claim that arbitrary middleware makes tools/provider sessions
supported. In particular, a function-invocation loop is outside this sample.
A cache **outside** the routed client can return a previous response without a
new invocation, verification or durable run; its old metadata is not evidence of
a new live execution. Provider wrappers belong on each route when behavior should
be attributed to that route. Revise route configuration identity for consequential
changes not represented by the declared route settings.

Use the normal streaming method too:

```csharp
await foreach (var update in chatClient.GetStreamingResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array."))
{
    Console.Write(update.Text);
}
```

Actual provider updates are forwarded unchanged: no synthetic diagnostic chunk
or metadata mutation. Quality is checked only on full supported text completion.
Breaking/disposing early records Abandoned; cancellation remains Cancelled.
After **any** update reaches the caller, failure cannot invoke an alternate route.

## Optional outcome information

Normal calls need no custom result or diagnostics service. Non-streaming responses
also carry immutable `RoutingResponseInfo` at `OutcomeRoutingChatClient.ResponseInfoKey`
(`OutcomeRouting.Result`). Existing provider metadata is retained; a collision
with that reserved key fails explicitly instead of overwriting it.
The information omits raw task text and projected state.

For an application that wants to inspect it:

```csharp
if (response.AdditionalProperties?.TryGetValue(
    OutcomeRoutingChatClient.ResponseInfoKey,
    out var value) != true || value is not RoutingResponseInfo info)
{
    throw new InvalidOperationException("Expected outcome-routing response information.");
}

Console.WriteLine($"Actual route: {info.ActualRoute}; outcome: {info.Feedback.Outcome}");
```

`chatClient.GetService<OutcomeRoutingChatClient>()` works through normal MEAI
wrappers. Its `GetRun`, `GetAttempts` and `ReportFeedback(runId, feedback)` methods
are optional application diagnostics/feedback, not invocation prerequisites.
Only report an independently known result when the completed run has no feedback;
conflicting/foreign/noncompleted reports fail. Identical replay is idempotent.
Missing/inapplicable verifiers leave Unknown and allow later application feedback;
an explicit Unknown verifier result remains immutable.

The aggregate client advertises its own provider name but **no single model or
provider URI**, and does not pretend to offer one arbitrary downstream provider's
services. Streaming deliberately adds no run-ID chunk; the advanced coordinator
is available for applications needing an explicit streaming run-ID contract.

## Configure a client without metadata

`ChatRoute.Create` reads MEAI `ChatClientMetadata.ProviderUri` and
`DefaultModelId`. These come from the concrete configured client, not the tier name.
Incomplete identity, or an endpoint containing credentials/query/fragment, is
rejected. Neither model quality nor provider capability is inferred from metadata.

Use the existing explicit constructor when your adapter cannot supply reliable
metadata. For example, given a caller-owned `IChatClient client`:

```csharp
var route = new ChatRoute(
    name: "fast",
    tier: Tier.Fast,
    capabilities: Capability.Text,
    providerEndpoint: "https://api.example.invalid/v1/",
    model: "your-configured-model",
    configRevision: "text-v1",
    client: client);
```

That endpoint is a placeholder, not a service to call. For actual provider wiring,
[LiveConfiguration.cs](../src/JuliaRouting.Sample/LiveConfiguration.cs) creates
the OpenAI adapter from explicit environment settings and disables SDK retries so
MEAI attempt telemetry remains understandable.

Route defaults are temperature 0 and a 128-token output budget, without reasoning
effort. Use `configuration: new RouteSettings(...)` if your model requires different
settings. A provider-side configuration change not visible in endpoint/model/options
requires a new `configRevision`, so unrelated evidence is not reused.
The optional `capabilities` declaration, not metadata, controls eligibility.

## What the routed client owns underneath

These files are **advanced implementation**, not things you must wire to use it:

| File | Responsibility |
|---|---|
| [OutcomeRoutingChatClient.cs](../src/OutcomeRouting/OutcomeRoutingChatClient.cs) | Standard MEAI contract, request projection and borrowed lifetime |
| [RoutingObservation.cs](../src/OutcomeRouting/RoutingObservation.cs) | Optional immutable decision/route/attempt observations and async observer contract |
| [Contracts.cs](../src/OutcomeRouting/Contracts.cs) | Typed tasks, outcomes, metadata-derived/explicit routes and configuration identity |
| [DecisionPolicy.cs](../src/OutcomeRouting/DecisionPolicy.cs) | Compact state, typed alternatives, deterministic bounded policy |
| [OutcomeRouter.cs](../src/OutcomeRouting/OutcomeRouter.cs) | Actual MEAI selection and attempt hooks |
| [OutcomeApplication.cs](../src/OutcomeRouting/OutcomeApplication.cs) | Durable request correlation, completion, verification and streaming cleanup |
| [ExactSortVerifier.cs](../src/OutcomeRouting/ExactSortVerifier.cs) | Independent toy transformation check |
| [SqliteOutcomeStore.cs](../src/OutcomeRouting/SqliteOutcomeStore.cs) | Transactional attempts and eligible completed evidence |

The full console's [Program.cs](../src/JuliaRouting.Sample/Program.cs) is now only a
dispatcher. Its [Arguments](../src/JuliaRouting.Sample/Arguments.cs),
[NativeScenario](../src/JuliaRouting.Sample/NativeScenario.cs),
[LiveConfiguration](../src/JuliaRouting.Sample/LiveConfiguration.cs),
[ClientSet](../src/JuliaRouting.Sample/ClientSet.cs), and
[Demonstrations](../src/JuliaRouting.Sample/Demonstrations.cs) keep their actual
responsibilities separate. The shared [asset check](../samples/Shared/NativeAssets.cs)
does not select routes or download models.

## Julia proposes; policy decides

The core depends on the actual vendored `IDecisionGenerator`, not Julia specifically.
Julia's provider returns finite-choice typed answers, not generated prose parsed
into labels:

```csharp
var result = await generator.GenerateAsync(
    state,
    Questions,
    RoutingJson.Default.DecisionState,
    cancellationToken: cancellationToken);

var answer = result.GetAnswer<ChoiceDecisionAnswer>(QuestionId);
```

`DecisionPolicy` validates the exact fast/balanced/strong distribution, then applies
confidence policy, application minimum, relevant known-failure floors, and declared
capabilities. Its snapshot keeps the original recommendation and probabilities
separate from the selected policy tier. The projection contains task text and is
caller-visible but not persisted by the store.

## MEAI executes; verification stays independent

`OutcomeRouter : FailoverChatClient : RoutingChatClient` uses the real MEAI loop.
Its selection code is readable independently of persistence:

```csharp
var route = _catalog.Routes
    .Where(route =>
        route.Tier >= session.Decision.Selected &&
        (route.Capabilities & session.Task.Required) == session.Task.Required &&
        !session.Invoked.Contains(route.ExecutionIdentity))
    .OrderBy(route => route.Tier)
    .ThenBy(route => route.Name, StringComparer.Ordinal)
    .FirstOrDefault()
    ?? throw new InvalidOperationException("No untried eligible route remains.");
```

Aliases of the same endpoint/model/options do not permit repeating a failed
physical invocation. The attempt hook exposes transport completion and output
commitment, **not the response or quality**. Only the application verifier examines
the completed output. A poor answer ends this request; it does not fabricate a
transport error to obtain another answer.

Known feedback is recorded against the actual completed route. Unknown, provider
failure, cancellation and partial streams are not positive evidence. Later reads
are bounded and isolated by cohort and configuration.

## Tests to read when adapting the API

[RoutingChatClientTests.cs](../tests/OutcomeRouting.Tests/RoutingChatClientTests.cs)
exercises full message/provider-response preservation, single-use sequences,
real middleware composition, services, borrowed/shared ownership, metadata identity,
persisted failure, options, feedback, concurrency, cancellation and exact streaming.
The existing [routing acceptance](../tests/OutcomeRouting.Tests/RoutingAcceptanceTests.cs),
[store](../tests/OutcomeRouting.Tests/OutcomeStoreTests.cs), and
[policy/verifier](../tests/OutcomeRouting.Tests/PolicyAndVerifierTests.cs) suites
retain the detailed cancellation, failover, concurrency, corruption and
configuration-isolation guarantees.

[RoutingObservationTests.cs](../tests/OutcomeRouting.Tests/RoutingObservationTests.cs)
checks observation ordering, correlation, concurrent runs, errors/cancellation,
history injection, unchanged responses/updates and no inferred quality.
[OllamaRoutingTests.cs](../tests/OutcomeRouting.Tests/OllamaRoutingTests.cs)
checks the actual pinned adapter using offline HTTP payloads, not a live server.
The [Ollama consumer](../samples/OllamaRouting/README.md) demonstrates provider
creation against that same `IChatClient` abstraction.

The [lifecycle reference](06-policy-and-lifecycle.md) describes the supported
stateless options and limits. The [native guide](04-native-julia.md) explains the
owning Julia generator's serialized native inference and stricter token limits.

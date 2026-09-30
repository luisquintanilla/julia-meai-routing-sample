# 5. Opt into real downstream chat

[Learning path](README.md) | Previous: [native Julia](04-native-julia.md) |
Next: [policy and lifecycle](06-policy-and-lifecycle.md)

This is an optional, potentially billable step. Complete the fixture demo and
native setup first. Adding `--live` changes downstream execution to real configured
chat clients; **Julia remains the local decision model**.

The sample does not provision a service, find credentials, download models, or
make a cloud call just because environment variables exist. `--live` is the only
network-inference path. No live endpoint was called during sample validation.

## Configure explicitly, without committing secrets

Use a service/endpoint and models you are authorized to call. The standard
`Microsoft.Extensions.AI.OpenAI` adapter supports the OpenAI API and compatible
chat endpoints, subject to each service's actual compatibility.

The following PowerShell uses a public API base URL and placeholders, **not usable
credentials or recommended model rankings**. Replace values locally.
Avoid putting real credentials into shell history or transcript files; supply the
API key from your local secret-management workflow to the process environment.
Do not commit a populated `.env` file. The app reads environment variables
directly and does **not** load `.env` automatically.

```powershell
$env:ROUTING_CHAT_ENDPOINT = "https://api.openai.com/v1/"
$env:ROUTING_CHAT_API_KEY = "<supply-from-your-local-secret-store>"
$env:ROUTING_FAST_MODEL = "<your-fast-model>"
$env:ROUTING_BALANCED_MODEL = "<your-balanced-model>"
$env:ROUTING_STRONG_MODEL = "<your-strong-model>"
dotnet run --project src\JuliaRouting.Sample -- --julia artifacts\decision-models\julia --live
```

Configure all three tiers using **at least two distinct model IDs**, each at most
64 characters. Fast/Balanced/Strong labels are application intent; they do not
prove the mapped models' relative quality, speed or price.

| Environment key | Required? | Meaning |
|---|---|---|
| `ROUTING_CHAT_ENDPOINT` | Yes | Absolute HTTPS API base URI; HTTP only for loopback |
| `ROUTING_CHAT_API_KEY` | Yes | Credential obtained locally; never part of route identity/history |
| `ROUTING_FAST_MODEL` | Yes | Model ID mapped to Fast |
| `ROUTING_BALANCED_MODEL` | Yes | Model ID mapped to Balanced |
| `ROUTING_STRONG_MODEL` | Yes | Model ID mapped to Strong |
| `ROUTING_FAST_REASONING` | No | Explicit `low`, `medium` or `high` effort, only if supported |
| `ROUTING_BALANCED_REASONING` | No | Same, for Balanced |
| `ROUTING_STRONG_REASONING` | No | Same, for Strong |

For a model that actually accepts explicit reasoning effort:

```powershell
$env:ROUTING_STRONG_REASONING = "high"
```

An absent effort means no explicit reasoning setting. Endpoint user-info,
query strings and fragments are rejected: credentials do not belong in a URL.
Missing live settings fail **before native loading and client creation**.

## What gets invoked?

[`LiveConfiguration.CreateClients`](../src/JuliaRouting.Sample/Program.cs) creates
the actual OpenAI SDK client and adapts each model using `AsIChatClient`.
SDK retries are disabled; MEAI's attempt ledger reflects configured route
invocations rather than hiding SDK-level retries.

[`ChatRoute`](../src/OutcomeRouting/Contracts.cs) uses real MEAI `ConfigureOptions`
to set model ID, temperature **0**, maximum output tokens **128**, and optional
reasoning effort. It removes application request metadata before forwarding.
Caller options are cloned, not mutated.

Those defaults are suitable only for models/endpoints that accept them. Some
reasoning models reject explicit temperature or use a different reasoning
contract. The optional live path does not certify every "OpenAI-compatible"
service. Inspect `RouteSettings`/`ChatRoute` and adapt the explicit options for
your provider; configuration changes must participate in route identity.
Do not silently drop unsupported settings and reuse old evidence.

The initial recommendation may select one route; a pre-output provider failure
may invoke an eligible alternate. Success/failure quality feedback belongs to
the **actual completed model/configuration**, not the initial recommendation.
HTTP/auth/configuration failures are not evidence of failed sorting quality.

## Privacy and cost

| Boundary | What happens |
|---|---|
| Local Julia | Task/context/current route summaries and bounded known evidence are scored inside the process |
| Live chat provider | Receives task/context messages and downstream chat options, including caller-supplied provider properties |
| SQLite | Stores run/cohort/config identity, decision distribution/reasons, attempt telemetry and terminal feedback; not raw task/output/credential fields by default |
| Console | Native mode prints projected decision state, local DB path and run identity; these can reveal task text or local metadata |

The decision history is not added to downstream chat messages by this sample.
Removing the internal routing key does not sanitize arbitrary caller/provider
extension values. Use **synthetic input while learning**, never employee/customer
data, secrets or proprietary prompts without your application's privacy controls.
An ignored local DB is still sensitive operational data; ignoring Git is not
encryption, access control, log redaction or consent.

Live providers may retain/log data under their own policies. Review those policies
and account permissions, set usage/billing controls, and remember that a failover
request can incur more than one provider invocation. No price or latency
optimization is established by this sample.

Default live history is `.routing\julia-live.db`, separate from native-fixture
history. `--store` permits a user-owned local path:

```powershell
dotnet run --project src\JuliaRouting.Sample -- --julia artifacts\decision-models\julia --live --store .routing\my-live.db
```

Custom databases, `.env` files, model assets, diagnostics and internal proof
artifacts are excluded from the candidate source publication. Keep them excluded
when packaging your own changes. Do not upload local transcripts blindly.

## Known limitations

Only short independent stateless text/JSON tasks are supported. Provider
conversation IDs, continuation tokens, background execution and tool controls
are rejected. There is no multi-turn agent or function-calling harness.
Capabilities in the catalog are declarations that the application must validate.

The exact-sort verifier remains a toy check, even with a real chat model.
Passing it does not establish general reliability, safe tool use, coding ability,
or an optimal tier mapping. A poor supported response is verified Failure and
ends the request; later known feedback may raise a future request's minimum tier.

No paid/remote calls were made during verification. For setup/provider errors,
see [troubleshooting](troubleshooting.md#live-chat-errors).

# Application API and advanced references

[Sample home](../README.md)

Start with the code you would write:

- [Offline application](../samples/GettingStarted/README.md): concrete client wiring,
  routed `IChatClient` creation, standard response calls, and a useful outcome.
- [Julia application](../samples/JuliaRouting/README.md): the same calls with a real
  owning local decision generator.
- [Ollama application](../samples/OllamaRouting/README.md): explicit local provider
  clients and real answers, with a clearly simulated decision signal.

The [API and code tour](03-code-tour.md) explains the MEAI contract and how to
configure existing `IChatClient` adapters. Read advanced internals only when you
want to adapt policy, verification, or persistence.
The examples expose `IDecisionGenerator`, `ChatRoute`/`IChatClient`, `IOutcomeStore`,
policy, request-specific verification and optional `IRoutingObserver`. Observation
is an intermediate decision/attempt view, not another response contract.

| Advanced reference | Purpose |
|---|---|
| [Concepts and glossary](01-concepts.md) | Decision versus chat model, transport versus quality, MEAI versus ML.NET |
| [Full outcome walkthrough](02-offline-walkthrough.md) | Five asserted reload/unknown/failover transitions |
| [Native Julia setup](04-native-julia.md) | Manual pinned assets, hashes, token budgets, native lifetime |
| [Live chat](05-live-chat.md) | Concrete model mapping, environment-only secrets, cost and privacy |
| [Policy and lifecycle](06-policy-and-lifecycle.md) | Bounds, identity, feedback, cancellation and streaming |
| [Troubleshooting](troubleshooting.md) | Explicit setup and runtime errors |

For .NET newcomers: an SDK compiles/runs the code; a runtime alone is insufficient.
`dotnet restore` acquires dependencies, `dotnet run --project ...` selects an
application, and arguments after `--` go to that application. `.csproj` files
describe references; `.slnx` groups the projects.

These are **Microsoft.Extensions.AI**, not ML.NET training APIs. Julia is pretrained.
You do not need a dataset, GPU, Python, or model training for the offline application.

# Julia + MEAI: outcome-aware routing

Create an `IChatClient` with routing configured, then use normal MEAI calls.
A known independent outcome can inform the **next request**.

## The code you write

This is the application-facing part of
[`samples/JuliaRouting/Program.cs`](samples/JuliaRouting/Program.cs). `assetDirectory`
is the explicitly supplied, hash-checked local Julia directory. The chat clients
below are **deterministic demonstrations**, not real services or quality benchmarks.

```csharp
using var decisions = JuliaDecisionGenerator.LoadFromDirectory(assetDirectory);
using var fast = new DemoChatClient("demo-fast", "[1,2,3]");
using var balanced = new DemoChatClient("demo-balanced", "[1,2,3]");
using var strong = new DemoChatClient("demo-strong", "[1,2,3]");

using IChatClient chatClient = new OutcomeRoutingChatClient(
    decisions,
    [
        ChatRoute.Create(Tier.Fast, fast),
        ChatRoute.Create(Tier.Balanced, balanced),
        ChatRoute.Create(Tier.Strong, strong)
    ],
    new OutcomeRoutingOptions
    {
        HistoryPath = @".routing\julia-quickstart.db",
        Cohort = "sort-integers-v1",
        VerifierFactory = SortVerification.ForMessages
    })
    .AsBuilder()
    .Build();

ChatResponse response = await chatClient.GetResponseAsync(
    "Sort [3,1,2] ascending. Return only the JSON array.");
Console.WriteLine(response.Text);
```

The return value is the **actual provider `ChatResponse`**, not a custom result.
Use ordinary `ChatOptions`, cancellation tokens, message history, and
`GetStreamingResponseAsync`. Normal `.AsBuilder()` middleware still composes.
The independent [sort check](samples/Shared/SortVerification.cs) applies only to
this exact input; other requests remain Unknown rather than receiving a false grade.

**`OutcomeRoutingChatClient` and its routing configuration belong to this sample**,
not to Microsoft.Extensions.AI. It uses MEAI's real `FailoverChatClient`, a bounded policy,
and transactional SQLite evidence. You do not construct a catalog, store, router,
request session, or application coordinator to make a chat call.

The caller owns `decisions` and the route clients. The routed client borrows them;
the `using` order disposes it first. Routes read the
endpoint and model from `IChatClient` metadata instead of repeating them. A client
without that metadata needs the [explicit route configuration](docs/03-code-tour.md#configure-a-client-without-metadata);
an unknown identity is an error, not a guessed default.

## Try it first

Install a [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Git](https://git-scm.com/downloads). From PowerShell:

```powershell
git clone https://github.com/luisquintanilla/julia-meai-routing-sample.git
Set-Location julia-meai-routing-sample
dotnet restore JuliaRouting.slnx --locked-mode
dotnet run --project samples\GettingStarted --no-restore
```

Open the complete [`GettingStarted/Program.cs`](samples/GettingStarted/Program.cs).
It has the same client mapping, construction, and MEAI calls as above. It replaces
Julia with a clearly labeled fixture so you need **no weights or credentials**:

```text
SIMULATED decisions and chat responses. Real routing, verification and persistence.
[3,1,2]
[1,2,3]
```

The first client's wrong answer is checked against the independently sorted input.
Its known failure raises the next request's minimum tier. It is **not retried for
quality inside the first request**. The example creates a fresh ignored database
per run, so the output is reproducible.

Restore can download **NuGet packages**. Neither application downloads models.
"Offline" means no network model inference, not that package installation never
uses the internet.

## Choose how far to go

| Application | What is real | Run / read |
|---|---|---|
| Developer quickstart | MEAI routing, verification, persistence; decisions and chat are fixtures | [Getting started](samples/GettingStarted/README.md) |
| The same API with Julia | Local CPU Julia decisions; chat is still simulated | [Julia routing](samples/JuliaRouting/README.md) |
| Advanced outcome scenario | Full failure/reload/unknown/failover demonstration; optional Julia and live chat | [Advanced walkthrough](docs/02-offline-walkthrough.md) |

After [preparing the pinned assets](docs/04-native-julia.md), run the small native
application with **one argument**:

```powershell
dotnet run --project samples\JuliaRouting -- artifacts\decision-models\julia
```

The existing advanced console remains available with its original commands:

```powershell
dotnet run --project src\JuliaRouting.Sample
dotnet run --project src\JuliaRouting.Sample -- --help
```

Its opt-in `--julia <directory> --live` mode uses explicitly configured real chat
clients. Review [live setup, billing, and privacy](docs/05-live-chat.md) before
using it. No live endpoint has been certified by these local demonstrations.

## What you choose; what the library handles

You choose the client-to-tier mapping. `OutcomeRoutingOptions` groups the history
path, nonidentifying cohort for comparable tasks, optional request-specific verifier
factory, capability/minimum tier, and bounded `PolicySettings` **at creation**.
Default configuration uses `.routing\history.db`, cohort `text-v1`, and no verifier.
No verifier means **Unknown**, not Success. Later independently known application
feedback is [optionally accessible](docs/03-code-tour.md#optional-outcome-information)
through typed response metadata and `GetService`, not required to call the client.

The library validates the decision distribution, applies policy, executes using
MEAI, records actual attempts, and correlates verified feedback to the completed
route. Only bounded, relevant known outcomes with the same configuration become
future evidence. Configuration changes isolate history; they never update Julia's
weights.

Fast / Balanced / Strong express intended cost/latency/capability tradeoffs, not
fixed models or guarantees. Evaluate your own mapping.

**MEAI is not ML.NET.** These routing APIs are in `Microsoft.Extensions.AI`, not
ML.NET's `Microsoft.ML` training / `IDataView` APIs. Julia is a pretrained
finite-choice decision model, **not a chat model**. It scores described alternatives
using ONNX Runtime; no training or fine-tuning is required.

## Implementation and boundaries

The [application API and implementation tour](docs/03-code-tour.md) explains the
routed `IChatClient` first, then the advanced internals. The [reference index](docs/README.md)
links concepts, native setup, policy/lifecycle, and troubleshooting; none is a
prerequisite to reading the quickstart.

Candidate probabilities are **not calibrated task-success chances**. Transport
completion is not verified success. The toy sort verifier is not production
semantic evaluation. After any streaming update reaches the caller, there is
**no mid-stream failover**; dispose streaming enumerators to preserve cleanup.
Use synthetic tasks: SQLite omits raw task/output/credentials by default, but
console output and real providers have separate privacy boundaries.

This sample supports complete system/user/assistant **text** history and forwards
the original messages, roles, content fragments and provider response unchanged.
For selection, all supplied text and roles must fit a **300-character projection**;
overflow fails before persistence or inference, with no truncation.
This is a sample policy limit, **not an MEAI limit or a token budget**.
Images, tools, opaque content, provider conversation IDs/continuations and background
responses are explicitly out of scope. See [composition and streaming](docs/03-code-tour.md#standard-meai-composition-and-streaming)
for middleware placement and the supported contract.

The Julia adapter strictly rejects oversized inputs: **1024 combined tokens,
256 head, 48 per option**, not upstream Python's newer 8k limit. Model assets are
not bundled. Windows x64 CPU is exercised; other native platforms remain unverified.

```powershell
dotnet build JuliaRouting.slnx --no-restore
dotnet test tests\OutcomeRouting.Tests\OutcomeRouting.Tests.csproj --no-restore
.\eng\Verify-Vendor.ps1
```

The [CI workflow](.github/workflows/ci.yml) restores locks, audits all transitive
packages against official NuGet vulnerability data with warnings as errors,
builds, runs the bounded tests and offline quickstart, and verifies the **15
unchanged vendor C#/project hashes**. It uses no provider credentials or model assets.

## Attribution and release rights

[Jevia](https://github.com/assistant-ui/jevia) inspired the outcome loop.
The real MEAI APIs are described in the
[routing and failover article](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/).
The vendored decision implementation is pinned to
[typesafe-meai commit 2266e935](https://github.com/luisquintanilla/typesafe-meai/commit/2266e935007b83391b9ea4c506ec6703b271372b);
see [provenance](vendor/README.md) and [file hashes](vendor/provenance.json).

This sample and its exact vendored C# code have owner-authorized [MIT](LICENSE)
distribution. The original revision had no root LICENSE; this does **not** claim
it was already MIT-licensed. Separate Julia artifacts retain Apache-2.0 according
to their pinned cards; dependencies have their own [notices](THIRD_PARTY_NOTICES.md).
No official Microsoft, Julia, or Jevia endorsement is implied.

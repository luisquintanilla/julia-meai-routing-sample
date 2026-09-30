# Julia + MEAI: learn outcome-aware model routing

**Choose a model route, check what it actually did, and use that outcome on the
next request.** This small .NET 10 console sample makes each step visible. Start
without model weights, credentials, a GPU, Python, or any model-service calls.

The default demonstration **simulates both decision signals and downstream chat
responses**. Its routing/failover APIs, application policy, independent checks,
and SQLite persistence are real. Native Julia and live chat are separate opt-ins.

> No official Microsoft, Julia, or Jevia endorsement is implied.

## Try it first

Install a [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Git](https://git-scm.com/downloads). The commands below use PowerShell and
Windows paths; Windows x64 is the native configuration exercised here.
No machine-learning background is required.

If you already have the source, open a terminal at its root. Once the repository
is published:

```powershell
git clone https://github.com/luisquintanilla/julia-meai-routing-sample.git
Set-Location julia-meai-routing-sample
dotnet restore JuliaRouting.slnx --locked-mode
dotnet run --project src\JuliaRouting.Sample --no-restore
```

Restore may download **NuGet packages**, including native runtime libraries.
The application does not download models. "Offline demo" means no network model
inference, not that first-time dependency acquisition never needs the internet.
It creates a fresh ignored `.routing\demo-<unique>\history.db` each time.

### What should you see?

The task is deliberately tiny: sort `[3,1,2]` and return only the JSON array.
Here are five lines from the actual run; the
[annotated walkthrough](docs/02-offline-walkthrough.md#captured-output) includes
the complete transition output and explains every column.

```text
1 initial: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Failure provenance=Verifier
2 after persisted failure/reload: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=balanced actual=balanced outcome=Success provenance=Verifier
3 explicit unknown: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Unknown provenance=None
4 after unknown: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=fast outcome=Unknown provenance=None
5 pre-output failover: signal=fast distribution=[fast:0.9, balanced:0.08, strong:0.02] policy=fast actual=balanced outcome=Success provenance=Verifier
```

The Fast fixture intentionally returns a bad answer first. An independent
verifier catches it. On a **later request**, persisted failure evidence makes
policy choose Balanced, even though the simulated recommendation is still Fast.
Unknown outcomes do not change policy. A separate simulated provider outage
demonstrates transport failover and correct attribution to the alternate route.
The application asserts these transitions before declaring the demo successful.

This is **not evidence that a real fast model is bad, that a strong model is
better, or that Julia is 90% accurate**. Those probabilities are fixture data.

## The idea in one picture

```text
task + comparable recent outcomes
          |
          v
decision signal          "Which minimum tier seems sufficient?"
          |
          v
application policy       "Which routes are allowed?"
          |
          v
MEAI execution           "Which client actually completed?"
          |
          v
independent verification "Did the result meet this task's checks?"
          |
          v
SQLite outcome evidence ---------> a later independent request
```

Fast / Balanced / Strong are stable application **capability tiers**, not
specific models or measured guarantees. They let you express intended
cost/latency/quality tradeoffs, then map each tier to your own concrete client.
You still need to evaluate that mapping for your tasks.

**MEAI is not ML.NET.** The routing APIs here belong to
`Microsoft.Extensions.AI`, not ML.NET's `Microsoft.ML` training or `IDataView`
APIs. Julia-1 is a pretrained **decision model** that scores supplied alternatives;
it is not a chat model. Native mode uses ONNX Runtime for inference and a native
tokenizer. No ML.NET training, fine-tuning, or Julia weight updates are involved.

## Follow the learning path

| Start here | What you will learn |
|---|---|
| [1. Concepts and glossary](docs/01-concepts.md) | Router, model, provider, client, decision, and policy without assuming ML knowledge |
| [2. Offline walkthrough](docs/02-offline-walkthrough.md) | Read the five transitions and distinguish transport health from task quality |
| [3. Code tour](docs/03-code-tour.md) | Follow the actual types and files from input to persisted feedback |
| [4. Run real Julia locally](docs/04-native-julia.md) | Pinned assets, manual setup, hashes, strict token budgets, and ownership |
| [5. Opt into live chat](docs/05-live-chat.md) | Environment-only settings, model mapping, billing, and data movement |
| [6. Policy and lifecycle reference](docs/06-policy-and-lifecycle.md) | Confidence, evidence windows, identity, feedback, cancellation, and streaming |
| [Troubleshooting](docs/troubleshooting.md) | Missing configuration, native libraries, token limits, and storage failures |

The [learning-path index](docs/README.md) suggests routes for .NET and ML newcomers.

## Choose what is real

| Command after restore | Decision signal | Downstream response |
|---|---|---|
| `dotnet run --project src\JuliaRouting.Sample` | **Fixture** | **Fixture** |
| Add `-- --julia artifacts\decision-models\julia` | **Real Julia CPU inference** | **Fixture** |
| Add `-- --julia artifacts\decision-models\julia --live` | **Real Julia CPU inference** | **Configured live chat service** |

Native assets are not included; read the [native guide](docs/04-native-julia.md)
before using those flags. `--live` is the only network-inference path and may
incur charges. Live endpoint compatibility has not been exercised.

## Check the implementation

```powershell
dotnet build JuliaRouting.slnx --no-restore
dotnet test tests\OutcomeRouting.Tests\OutcomeRouting.Tests.csproj --no-restore
.\eng\Verify-Vendor.ps1
dotnet run --project src\JuliaRouting.Sample -- --help
```

The native-independent suite has **140 passing cases** covering policy, actual
route attribution, concurrency, persistence, option isolation, cancellation,
streaming commitment, and failure cleanup. The vendor check verifies all **15
unchanged C#/project files** against the pinned source manifest. Validation used
Windows x64 and .NET SDK 10.0.401; other native platforms are not certified.

The [CI workflow](.github/workflows/ci.yml) runs this bounded suite on Windows with
.NET 10. It restores the locks from official NuGet, audits direct and transitive
packages against NuGet's official vulnerability data, and fails on audit warnings
(including unavailable data). It requires no provider secrets, Julia assets, or
live model calls.

## Important boundaries

Candidate probabilities are **not calibrated task-success chances**. A completed
response is not a verified success. A poor answer ends its request; feedback can
affect the next one, not silently trigger a same-request quality cascade.
After any streaming update reaches the caller, there is **no mid-stream failover**.
The toy verifier demonstrates a checkable transformation, not production semantic
evaluation or automatic optimal routing.

This is a compact teaching sample: no agent framework, coding subprocess harness,
dashboard, route cache, exploration algorithm, distributed history, or training.
Use synthetic tasks while learning. Local SQLite omits raw prompts, output, and
credentials by default, but console output and a live provider have separate
[privacy boundaries](docs/05-live-chat.md#privacy-and-cost).

## Attribution and release rights

The outcome loop borrows concepts from [Jevia](https://github.com/assistant-ui/jevia).
The MEAI implementation follows its
[routing and failover primitives](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/).
Julia/abstractions/ONNX source is pinned to
[typesafe-meai commit 2266e935](https://github.com/luisquintanilla/typesafe-meai/commit/2266e935007b83391b9ea4c506ec6703b271372b).
See [source and dependency provenance](vendor/README.md) and the
[hash manifest](vendor/provenance.json).

This sample and its exact vendored C# implementation are distributed under the
[MIT license](LICENSE), with source-owner authorization for this publication.
The original pinned source had no root LICENSE; this is **not** a claim that the
upstream revision was already MIT-licensed. The separate pinned Julia model cards
declare Apache-2.0 artifacts. Model assets and dependency binaries are not bundled;
their independent licenses are listed in [third-party notices](THIRD_PARTY_NOTICES.md).

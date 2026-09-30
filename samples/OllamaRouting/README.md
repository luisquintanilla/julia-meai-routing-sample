# Real Ollama answers, with visible routing decisions

[Program.cs](Program.cs) creates real `OllamaApiClient` instances typed as
`IChatClient`, declares `ChatRoute` mappings, and composes the same history,
policy, verifier, observation and standard MEAI builder as the other consumers.
It uses a **simulated decision signal** so Julia assets are not a prerequisite.
Chat answers are real model output, not fixture answers or a quality benchmark.

## Explicit local prerequisites

Install [Ollama](https://ollama.com/download), then start its local daemon
(`ollama serve` if it is not already running). Choose models that fit your machine
and review their licenses. Any acquisition is **your separate, explicit action**:

```powershell
ollama pull llama3.2:3b
ollama list
```

That command downloads weights only if **you run it**. The .NET application never
pulls a model, starts Ollama or obtains credentials. Copy exact `model:tag` names
from `ollama list`. The endpoint must be a loopback HTTP(S) base URL without
credentials/path/query/fragment; known `-cloud` tags are rejected. Ensure your
daemon serves local weights rather than cloud/proxy aliases; a loopback address
alone is not a guarantee about the daemon's behavior.

After the usual solution restore, from the repository root:

```powershell
dotnet run --project samples\OllamaRouting -- `
  http://localhost:11434/ llama3.2:3b llama3.2:3b llama3.2:3b
```

Using one installed model for all three labels is a small **connectivity example**,
not three capability levels. For meaningful routing, provide your three evaluated
model mappings in Fast/Balanced/Strong order instead. Equal endpoint/model/options
are physical aliases; failover will not call the same model under another label.
There is no universal ranking or calibrated downstream-success probability.

## The actual provider abstraction

The concrete provider creation in the runnable source is:

```csharp
using IDecisionGenerator decisions = new FixtureDecisionGenerator();
using IChatClient fast = new OllamaApiClient(settings.Endpoint, settings.FastModel);
using IChatClient balanced = new OllamaApiClient(settings.Endpoint, settings.BalancedModel);
using IChatClient strong = new OllamaApiClient(settings.Endpoint, settings.StrongModel);
```

`OllamaSharp` **5.4.30** implements `IChatClient` directly; no `AsChatClient`
extension or sample-written provider wrapper is required. This stable version
restores from the available official public feed and was checked against
[its immutable source](https://github.com/awaescher/OllamaSharp/tree/b1b408df43a2a13a29e8db9de3130fa6f44aacc9)
and the installed package. MEAI remains pinned at 10.10.0. Microsoft documents
the same [direct client construction](https://learn.microsoft.com/dotnet/ai/quickstarts/chat-local-model).
`ChatRoute.Create` reads the adapter's endpoint/model metadata; do not mutate
`SelectedModel` after route creation.

`OutcomeRoutingChatClient`, `ChatRoute`, `OutcomeRoutingOptions`, `IOutcomeStore`,
and `IRoutingObserver` are this repository's shared library. `OllamaSettings`,
`ConsoleRoutingObserver`, and the exact sort selector are small sample components.
The standard builder, client, options, response and updates belong to MEAI.

The sample first lists existing models through the provider's inventory API.
Missing tags stop execution **before history/inference/chat** with an explicit
setup error, without a pull or fixture fallback. An unavailable endpoint or
later provider error returns exit code **2** with safe troubleshooting guidance.
The two-minute deadline bounds the preflight/request cooperatively. Other errors
also surface; transport completion is not recorded as verified sorting success.

## Answer and observation

Normal mode calls `GetResponseAsync` and prints `ChatResponse.Text`. Before the
answer, the console observer reports recommendation/distribution, policy tier and
selected route, then the actual completed attempt. Failed attempts/reselection
have their own correlated records. The independent verifier checks the known
sorting input only; it is neither another model's grading nor general quality.

For standard streaming:

```powershell
dotnet run --project samples\OllamaRouting -- `
  http://localhost:11434/ llama3.2:3b llama3.2:3b llama3.2:3b --stream
```

Provider `ChatResponseUpdate` objects are forwarded without synthetic telemetry
updates. Console observation is a separate side effect, not chat content.
Text plus usage telemetry can be independently checked only after full completion;
unsupported reasoning/tool content remains Unknown. Any delivered update commits
output, so there is no mid-stream alternate. Early disposal is Abandoned, not Success.

The prompt is the fixed, short sort task. The same complete-text-history
**300-character routing projection bound** applies; this is a sample limit, not
MEAI's or Ollama's context window. Images, tools, provider continuations and
background responses remain explicitly unsupported. Models must accept the
configured temperature 0, 128-output-token budget and TopP 0.9.

Each run uses a fresh ignored `.routing\ollama-<run>.db`. Mutable Ollama tags
must not silently reuse old evidence after their weights change. For persistent
application history, pin model configurations and revise `configRevision` when
provider/model behavior changes; the bounded evidence store is not a model registry.

## Validation boundary

No live Ollama daemon or model was called during implementation. Tests exercise
the **actual OllamaSharp adapter** against an in-process `HttpMessageHandler`,
including metadata, `/api/tags`, `/api/chat`, options, messages, usage, streaming,
missing models and endpoint errors. They never access a socket or download weights.
CI compiles this application and runs `--help`; real model output, hardware fit
and model/daemon compatibility require your opt-in local run.

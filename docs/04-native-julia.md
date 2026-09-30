# 4. Replace the decision fixture with real local Julia

[Learning path](README.md) | Previous: [code tour](03-code-tour.md) |
Next: [live chat](05-live-chat.md)

The default demo already exercises the pipeline. This optional step changes
**only the decision signal**: Julia runs locally on CPU, while chat responses
remain visibly simulated. It does not train a model or require Python, the Julia
programming language, a GPU, or a paid provider.

## Know exactly what is being loaded

Julia-1 is a pretrained, multilingual, approximately 144.3M-parameter finite-choice
decision model. The .NET provider uses its ONNX graph through CPU ONNX Runtime
(ORT), plus the pinned native `Tokenizers.HuggingFace` binding.
The export README describes browser WebGPU; **this sample does not run its browser
adapter**.

| Pin | Primary source |
|---|---|
| ONNX graph/weights/tokenizer | [Julia-1-ONNX @82a2fad](https://huggingface.co/SupersonicLabs/Julia-1-ONNX/tree/82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8) |
| Decision semantics | [Julia-1 @a85b127](https://huggingface.co/SupersonicLabs/Julia-1/tree/a85b127321d580d65176c89ced8273f305745d85) |
| C# provider source | [typesafe-meai @2266e935](https://github.com/luisquintanilla/typesafe-meai/tree/2266e935007b83391b9ea4c506ec6703b271372b/src/DecisionInference.Julia) |

The pinned model cards declare **Apache-2.0 model artifacts**. Review their license
before obtaining/redistributing them; the sample's MIT source license does not
relicense model files. Model assets are not in this repository.

Allow disk space for approximately **577 MB of external FP32 weights**, plus graph,
tokenizer, NuGet packages and runtime memory. The 577 MB disk figure is **not a RAM
requirement estimate**. Windows x64 CPU was exercised; other native platforms
require compatible ORT and tokenizer binaries and are unverified here.

## Prepare an explicit directory

The small [Julia application](../samples/JuliaRouting/Program.cs) accepts only the
asset directory. After the setup below:

```powershell
dotnet run --project samples\JuliaRouting -- artifacts\decision-models\julia
```

The original advanced `--julia` command additionally prints packing diagnostics.
Both use the same hash-checked local generator; neither downloads assets.

```text
artifacts\decision-models\julia\
  model.onnx
  model.onnx.data
  tokenizer.json
```

The graph references its adjacent `.data` file. Keep all three matching files
together and unchanged while loaded. Git LFS pointer text is not a model file.

**Manual acquisition is optional and substantial.** The following PowerShell
downloads approximately 577 MB of weights plus the other two files. Run it only
after reviewing the upstream license and choosing to acquire the pinned assets.
The application never runs these download commands implicitly.

```powershell
$revision = "82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8"
$base = "https://huggingface.co/SupersonicLabs/Julia-1-ONNX/resolve/$revision"
$assets = "artifacts\decision-models\julia"
New-Item -ItemType Directory -Force $assets | Out-Null
foreach ($file in "model.onnx", "model.onnx.data", "tokenizer.json") {
    $destination = Join-Path $assets $file
    if (Test-Path $destination) { throw "Refusing to overwrite an existing asset: $destination" }
    Invoke-WebRequest "$base/$file" -OutFile $destination
}
Get-FileHash "$assets\model.onnx", "$assets\model.onnx.data", "$assets\tokenizer.json" -Algorithm SHA256
```

Compare all hashes before using the files; stop on any mismatch:

| File | Required SHA256 |
|---|---|
| `model.onnx` | `97141d0cfb1da6204e9f8f24d581af72eaeb82cda21149d83eaa6df7160fbcd9` |
| `model.onnx.data` | `fd915be810d7ebfb80fb05a48dd33c9484d17ae1b6bcb9e1f544cbaaa913ded1` |
| `tokenizer.json` | `609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f` |

The executable independently requires these hashes before loading. A newer export
is not implicitly compatible. If acquisition was interrupted, inspect only your
chosen asset files and retry acquisition yourself; no automatic download/repair
manager is part of the sample.

## Run native decisions with fixture chat

From the repository root after restore:

```powershell
dotnet run --project src\JuliaRouting.Sample --no-restore -- --julia artifacts\decision-models\julia
```

This makes one sorting request through the same routing/policy/verifier pipeline.
It prints the full decision distribution, selected/actual route, independent
feedback, exact projected state, and packed token count.

Captured from real Windows x64 CPU inference with the pinned files:

```text
All three Julia asset SHA256 hashes match the pinned export.
REAL Julia-1 CPU decision inference; SIMULATED downstream chat responses.
native: signal=balanced distribution=[fast:0.0649368, balanced:0.825774, strong:0.10929] policy=balanced actual=balanced outcome=Success provenance=Verifier
  reasons=decision-signal; attempts=balanced:completed
Julia budget: 186 combined tokens; 192 padded tokens; limits 1024 total / 256 head / 48 per option.
```

Local run IDs, paths and the printed state are omitted above. The chat answer was
a fixture; only Julia decision inference was real model execution.
This one invocation is **not** a routing accuracy, calibration, cost or latency
benchmark. Later runs may use bounded history from the native database and give
different decisions.

Default native-fixture history is `.routing\julia-fixture.db`. To use a fresh,
separate history file, choose a new local path:

```powershell
dotnet run --project src\JuliaRouting.Sample -- --julia artifacts\decision-models\julia --store .routing\my-julia.db
```

`--no-verifier` leaves quality unknown. It does not mark successful transport as
known success. Demo history and live history are separate by default.

## Token limits are not character counts

The vendored .NET adapter enforces:

| Part | Strict limit |
|---|---|
| Entire combined sequence | **1,024 tokens**, including framing/state/question/options |
| Question-and-options head | **256 tokens**, with additional packing constraints |
| Each option description | **48 tokens** |
| Choice/Score candidates | **2-20** |

The upstream Python card advertises newer 8,192-token runtime support. **That is
not enabled by this .NET adapter.** Do not raise the sample's advertised limit by
quoting the Python card.

Tasks are limited to 300 characters and context to 120; evidence is bounded and
structured. Those limits reduce input size but do not guarantee token fit,
especially across languages. Framing, descriptions, route summaries and history
all consume space. Julia packing is authoritative and rejects overflow,
reserved framing tokens, invalid alternatives or unsupported tokenizer settings.
It never silently truncates the task to make inference succeed.

The demo native request used 186 active combined tokens and was padded to 192.
Padding aligns tensor shape; it does not add semantic task evidence.

## Lifetime and asynchronous caveats

[`JuliaDecisionGenerator.LoadFromDirectory`](../vendor/src/DecisionInference.Julia/JuliaDecisionGenerator.cs)
loads an **owning** generator. The small application's `Program.cs` holds one
instance for the run, then disposes its ORT scorer/session and native tokenizer.
The advanced console keeps that lifetime in
[`NativeScenario.cs`](../src/JuliaRouting.Sample/NativeScenario.cs). A service should likewise
load once, reuse it, and dispose on shutdown. Do not dispose borrowed/in-flight
resources or mutate assets while inference is running.

Preparation and inference are serialized. Returning `Task<DecisionResult>` does
not make synchronous native work nonblocking. Cancellation requests ORT
termination cooperatively; it is not a hard real-time guarantee.
The advanced console's 60-second request deadline starts **after synchronous model
loading**. Its two-thread setting is illustrative, not a performance recommendation.
The small application uses provider defaults; neither path is a benchmark.

No weights are changed, and there is no automatic fine-tuning, training-data
collection or ML.NET training pipeline.

The vendored references keep ORT/Managed 1.23.2, Tokenizers.HuggingFace 3.23.1 and
Tensors 10.0.9. MEAI 10.10.0 requires Tensors >=10.0.12, so the final executable
resolves **10.0.12** without a downgrade suppression. This build adaptation is
recorded in [provenance](../vendor/provenance.json).

For load errors, see [troubleshooting](troubleshooting.md#native-julia-errors).

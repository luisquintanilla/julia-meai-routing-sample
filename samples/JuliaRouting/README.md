# The same application API, with real Julia

[Program.cs](Program.cs) visibly loads Julia and maps three clients before calling
constructing `OutcomeRoutingChatClient` and calling standard MEAI `GetResponseAsync`.
Only asset/error handling
surrounds that application code. No CLI framework, token diagnostics, history
projection, route hashing, or outcome-store coordination appears in the entrypoint.

Prepare the [pinned local assets](../../docs/04-native-julia.md) first. Then, from
the repository root:

```powershell
dotnet run --project samples\JuliaRouting -- artifacts\decision-models\julia
```

Missing/incorrect assets fail explicitly; there is no download or fixture fallback.
Native ONNX Runtime and the tokenizer own resident native resources, so loading
and disposal are visible. The routed client **borrows** that generator.

One exercised Windows x64 run ends with:

```text
REAL Julia decisions. SIMULATED chat responses.
[1,2,3]
```

The three hash checks print a confirmation before these lines. The exercised
request selected Balanced and passed independent verification; the main code
needs only `ChatResponse.Text`. [Optional typed routing metadata](../../docs/03-code-tour.md#optional-outcome-information)
supports diagnostics without changing that call. Julia's selected
tier is not a benchmark or a promise for other inputs. History persists at
`.routing\julia-quickstart.db`; a known failure can influence later requests.
Chat clients are deterministic demonstrations and make **no paid/network calls**.

**Try one change:** replace the task with another short, independently checkable
sorting input and update [the request-specific check](../Shared/SortVerification.cs)
and demonstration responses together. Unrecognized requests intentionally have
no verifier, not a pretend sort-quality grade.
For a different task family, use a new nonidentifying cohort. Do not keep the
sort verifier for an arbitrary task or interpret Unknown as Success.

Real chat adapters fit the same `IChatClient` route mapping. The existing
[advanced live configuration](../../src/JuliaRouting.Sample/LiveConfiguration.cs)
shows concrete OpenAI client creation; consult [billing/privacy](../../docs/05-live-chat.md)
before opting in. Metadata must contain the actual endpoint and model, and route
defaults must suit those models. No real endpoint compatibility is asserted.

Candidate probabilities are relative to the proposed choices, **not task-success
chances**; no weights change. Supported complete text/role history must fit the
sample's 300-character selection projection, without truncation. That is separate
from the native token limits and not a limitation of MEAI itself.

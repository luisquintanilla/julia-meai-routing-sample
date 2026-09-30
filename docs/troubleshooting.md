# Troubleshooting

[Sample home](../README.md) | [Learning path](README.md)

Run commands from the repository root. The console returns **exit code 2** for an
error. Missing assets/configuration are errors, not uncertain model predictions.
There is no silent fallback to fixtures, default success, or empty history.

## SDK and basic commands

| Symptom | Check / action |
|---|---|
| `dotnet` is missing, or only a runtime is installed | Install a .NET 10 **SDK**; run `dotnet --version` |
| Project cannot be found | Change to the directory containing `JuliaRouting.slnx`, then use the documented project path |
| Package restore fails | Check NuGet connectivity/access; use `dotnet restore JuliaRouting.slnx --locked-mode`. This is dependency acquisition, not a model download |
| CLI rejects an option | Run `dotnet run --project src\JuliaRouting.Sample -- --help`; app flags belong after `--` |
| `--store`, `--no-verifier`, or policy flags fail in default mode | Those flags require `--julia`; the offline demo intentionally has isolated data and fixed asserted settings |
| Experimental MEAI diagnostic | MEAI001 is expected for routing types. Suppression is local to `OutcomeRouter.cs`; do not disable all warnings solution-wide |

The local empty `Directory.Build.targets` prevents unrelated ancestor build
targets from being imported. Do not copy another repository's build infrastructure
or remove version pins to make this sample appear green.

`NuGet.Config` uses NuGet's official vulnerability-data-only endpoint for audits,
independently of your package download feeds. Audit errors are not a clean result:
check access to `data.nuget.org` and do not suppress NU1900-NU1905 to hide a failed
audit or vulnerable package. A clean audit only covers known reported package
vulnerabilities, not every possible defect.

## Native Julia errors

| Symptom | Check / action |
|---|---|
| "Julia asset directory not found" | Pass an existing explicit directory after `--julia` |
| Missing `model.onnx`, `.data`, or `tokenizer.json` | Follow the [pinned native setup](04-native-julia.md#prepare-an-explicit-directory); no file is downloaded automatically |
| SHA256 mismatch | Stop. Compare all three files to the pin; inspect failed/incomplete acquisition or a Git LFS pointer. Do not bypass the check |
| Native library/architecture load error | Both CPU ORT and the Hugging Face tokenizer need compatible native binaries. Windows x64 is exercised; other platforms are unverified |
| ORT cannot resolve external data | Keep the matching `model.onnx.data` beside its referencing graph, with read permissions |
| Token/head/option budget exceeded | Shorten the actual task/context/history/description thoughtfully. This adapter permits 1024 total / 256 head / 48 option tokens, not upstream Python's newer 8k setting |
| Reserved token rejected | Avoid model framing-token literals such as `<mask>` in caller state/instructions/options; they are not ordinary prompt text |
| Inference seems blocking | `GenerateAsync` serializes synchronous native inference; `await` does not make it nonblocking. Resident reuse avoids repeated loading but is not a latency guarantee |
| Cancellation/load takes longer than expected | Request cancellation is cooperative; synchronous loading occurs before the console request deadline |

Do not silently truncate important task data or substitute a different model to
avoid an error. A task's character limits are not a guaranteed token fit.
Check the [lifetime/limits guide](04-native-julia.md#token-limits-are-not-character-counts).

## Live chat errors

Live chat requires both `--julia <directory>` and `--live`. The exact required
environment keys and optional reasoning keys are in the
[live guide](05-live-chat.md#configure-explicitly-without-committing-secrets).

| Symptom | Check / action |
|---|---|
| "Missing live configuration" | Supply the named environment setting to the process; `.env` files are not automatically loaded |
| Invalid endpoint | Use an absolute HTTPS API base URI (HTTP only for loopback), with no credentials/user-info, query or fragment |
| Too few distinct model IDs | Set all three tier variables with at least two distinct IDs supported by your endpoint |
| Unsupported temperature/reasoning/options | Confirm each concrete model's contract. Route defaults are explicit sample settings, not universal provider support |
| Generic provider execution failure | Confirm authentication, endpoint, model access, option support and service availability in your authorized provider tooling; avoid logging secrets/request bodies |
| No eligible alternate | Routes must satisfy the policy floor/capabilities and cannot repeat the same endpoint/model/options; a metadata alias is not another fallback |

Provider/auth/timeout failures are transport failures, not semantic sorting
failures. The app does not claim a failed provider attempt as known task evidence.
After streaming output has reached the caller, failure is terminal.

## Storage and feedback errors

| Symptom | Check / action |
|---|---|
| Cannot create/write a database | Check permissions/free space for `.routing` or your explicit `--store` path |
| Loaded SQLite regression fails | Restore the locked Microsoft.Data.Sqlite 10.0.12 / SQLitePCLRaw 2.1.12 graph and rebuild; check for stale native binaries. The test queries the loaded runtime and requires SQLite >=3.50.2 |
| SQLite busy timeout | Diagnose concurrent writers/long external transactions; sample writes are serialized with a bounded timeout |
| Unsupported schema or failed integrity check | Stop and preserve the user-owned DB. Inspect or restore it explicitly; the app never resets it to empty history |
| Feedback rejected for run/route | Use a local Completed run and its stored actual route identity; failed/cancelled/abandoned/foreign runs are ineligible |
| Conflicting terminal feedback | Explicit feedback is immutable. Identical replay is idempotent; Success/Failure/Unknown cannot be silently overwritten |
| No known evidence despite completed requests | Completion alone is not verification. A missing verifier or explicit Unknown remains unknown |
| Old evidence is not being applied | Check cohort, current catalog/config identity, history age and eligible-evidence limit |
| Run remains Running after process crash | It is excluded from evidence. No automatic recovery operation fabricates task completion |

Do not delete or rewrite a database simply because it is inconvenient. To start
an independent learning experiment, choose a **new** user-owned `--store` path.
The fixture demo already creates a fresh path each run.

## Rejected stateless options and partial streams

The application does not support provider conversation IDs, continuation tokens,
background responses or tool controls. A `ChatOptions.ResponseFormat` also needs
the task's declared Json capability. These inputs fail explicitly before invocation;
they are not forwarded in hopes that the provider will interpret them safely.

Always dispose streaming enumeration. A disposed early stream is Abandoned, not
known Success, even if the partial text looks plausible. Completed empty or
unsupported streams are explicitly Unknown. Completed supported text can be
independently verified, subject to the accumulation bound.

## Diagnose without leaking data

Share the command shape, exception **type**, nonidentifying configuration names,
and reproducible synthetic input first. Never attach real API keys, populated
environment files, local operational databases, native assets, corporate paths,
or unreviewed console transcripts. Native mode prints projected task state;
review it before sharing. Use the fixture demo/tests to narrow pipeline problems
without paid or sensitive-data calls.

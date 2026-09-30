# A learning path for outcome-aware routing

[Sample home](../README.md)

You do not need to train a model to understand this sample. Start with a small,
observable task and add one boundary at a time.

| Step | Read | Do |
|---|---|---|
| 1 | [Concepts and glossary](01-concepts.md) | Separate "choosing an executor" from "solving a task" |
| 2 | [Offline walkthrough](02-offline-walkthrough.md) | Run the fixture demo and predict the next tier before reading its output |
| 3 | [Code tour](03-code-tour.md) | Follow the same task through decision, policy, execution, verification, and storage |
| 4 | [Native Julia](04-native-julia.md) | Optionally replace only the decision fixture with real local inference |
| 5 | [Live chat](05-live-chat.md) | Optionally replace downstream fixtures after reviewing cost and privacy |
| 6 | [Policy and lifecycle](06-policy-and-lifecycle.md) | Understand bounds and failure cases before adapting the sample |

**New to .NET?** First use the [root quickstart](../README.md#try-it-first).
`dotnet restore` obtains packages, `dotnet build` compiles projects, and
`dotnet run --project ...` starts the console app. Arguments after `--` are passed
to the app, not to the .NET CLI. A `.slnx` groups the projects; `.csproj` files
describe each project's references. You need an SDK, not just a .NET runtime.

**New to model routing or ML.NET?** Read steps 1 and 2 before the APIs. The sample
uses **Microsoft.Extensions.AI (MEAI)**, not ML.NET's `Microsoft.ML` training APIs.
Its decision model is pretrained; there is no dataset-training pipeline to set up.

**Already use `IChatClient`?** Start with the [code tour](03-code-tour.md), then
the [policy/lifecycle reference](06-policy-and-lifecycle.md). The unusual part is
not forwarding chat: it is ensuring that evidence belongs to the actual completed
route and is independently known.

**Only want local Julia?** Complete the offline run first. The
[native guide](04-native-julia.md) then swaps one seam; downstream chat remains
clearly simulated. Native/live setup is never required for the fixture demo.

[Troubleshooting](troubleshooting.md) covers setup and runtime failures. Source
redistribution and dependency/model licenses are documented separately in the
[vendor notes](../vendor/README.md); they are not settled by an example command.

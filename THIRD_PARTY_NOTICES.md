# Third-party sources and licenses

## Vendored decision implementation

`vendor/src` contains the exact dependency closure for
`DecisionInference.Abstractions`, `DecisionInference.Onnx`, and
`DecisionInference.Julia` from
[luisquintanilla/typesafe-meai at 2266e935007b83391b9ea4c506ec6703b271372b](https://github.com/luisquintanilla/typesafe-meai/tree/2266e935007b83391b9ea4c506ec6703b271372b/src).
The 12 C# files and three project files are unchanged. Their hashes are in
[vendor/provenance.json](vendor/provenance.json).

The source owner authorized **MIT distribution of this sample and the pinned
vendored C# implementation in this publication**. The applicable license is
[LICENSE](LICENSE), also included at [vendor/LICENSE](vendor/LICENSE), with
copyright attribution to Luis Quintanilla. Existing source notices are preserved.
The original revision had no root LICENSE; the authorization here does not
claim that the upstream revision was previously MIT-licensed or change that
repository's files.

## Optional Julia model artifacts

Model weights, the ONNX graph, and the model tokenizer JSON are **not bundled**.
The separately acquired pinned artifacts retain their own Apache-2.0 licensing
according to the model cards:

- [Julia-1-ONNX, revision 82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8](https://huggingface.co/SupersonicLabs/Julia-1-ONNX/blob/82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8/README.md).
- [Julia-1 semantics, revision a85b127321d580d65176c89ced8273f305745d85](https://huggingface.co/SupersonicLabs/Julia-1/blob/a85b127321d580d65176c89ced8273f305745d85/README.md).

The sample's MIT license does not relicense those artifacts. Review and retain
the upstream license/notices if acquiring or redistributing them.

## NuGet dependencies

Dependencies are acquired through NuGet, not copied into this source repository.
Reviewed package metadata and bundled licenses include:

| Dependency | Version used | License |
|---|---|---|
| Microsoft.Extensions.AI / Abstractions / OpenAI | 10.10.0 | MIT |
| Microsoft.ML.OnnxRuntime / Managed | 1.23.2 | MIT; package-carried `LICENSE` / `LICENSE.txt` |
| Tokenizers.HuggingFace | 3.23.1 | Apache-2.0 |
| System.Numerics.Tensors | Vendor reference 10.0.9; executable resolves 10.0.12 | MIT |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| SQLitePCLRaw bundle/core/provider/native library | 2.1.12 | Apache-2.0 package metadata; retain native SQLite distribution notices |
| OpenAI .NET SDK | 2.13.0 (transitive) | MIT |
| xUnit v3 / Visual Studio runner | 3.2.2 / 3.1.5 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |

Lock files record the resolved transitive graph. Package/native licenses and
notices remain authoritative for their components. Before redistributing compiled
binaries, retain all required dependency/native notices and review the complete
resolved graph; this source-level summary is not a binary redistribution bundle
or legal certification.

Microsoft.Data.Sqlite 10.0.12 selects SQLitePCLRaw 2.1.12, including the native
`SQLitePCLRaw.lib.e_sqlite3` asset. This replaces vulnerable 2.1.11; see
[GHSA-2m69-gcr7-jv3q / CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
The Windows x64 validation loads SQLite **3.53.3** through a real connection and
`SELECT sqlite_version()`. A regression requires at least the **3.50.2** fix, not
just a managed package version. Other platforms' native execution remains unverified.

## Conceptual attribution

[Jevia](https://github.com/assistant-ui/jevia) inspired the outcome-loop design.
Only concepts are borrowed; its Rust CLI/harness implementation is not copied.
Its source declares MIT. Microsoft.Extensions.AI routing is used through the
actual NuGet API, not a copied replacement implementation.

Names identify their respective projects, not endorsement of this sample.

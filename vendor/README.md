# Pinned source and license review

The source under `src` was extracted from git objects at
[luisquintanilla/typesafe-meai@2266e935007b83391b9ea4c506ec6703b271372b](https://github.com/luisquintanilla/typesafe-meai/tree/2266e935007b83391b9ea4c506ec6703b271372b),
not from the source worktree's mutable files. The dependency closure is:

```text
DecisionInference.Abstractions (5 .cs + project)
  <- DecisionInference.Onnx (MarkerOnnxRunner.cs + project)
  <- DecisionInference.Julia (6 .cs + project; also references abstractions)
```

[provenance.json](provenance.json) records the source SHA256 hashes and the
necessary root build adaptations. Original source/project files are untouched.
The source revision's root tree contained no `LICENSE`. Its owner has explicitly
authorized **MIT distribution of this sample and the exact pinned vendored C#
implementation in this publication**. [LICENSE](LICENSE) records that grant;
[third-party notices](../THIRD_PARTY_NOTICES.md) explain the scope and attribution.
Existing notices are preserved. This does not claim an earlier MIT license for
the upstream revision or modify the upstream repository.

Reviewed model cards:

- [Julia-1-ONNX @82a2fad](https://huggingface.co/SupersonicLabs/Julia-1-ONNX/blob/82a2fadf8fccfccdc5fd4e1009ba8f1a265eb7a8/README.md):
  Apache-2.0 model artifacts; browser adapter description does not change this
  sample's CPU ORT boundary.
- [Julia-1 @a85b127](https://huggingface.co/SupersonicLabs/Julia-1/blob/a85b127321d580d65176c89ced8273f305745d85/README.md):
  Apache-2.0 model artifacts; no training pipeline is included.
- Jevia's MIT source license was reviewed; only the outcome-loop concepts are
  borrowed, not its Rust CLI/harness implementation.

Reviewed NuGet metadata/bundled notices for the actual pinned dependencies:

| Dependency | License in package metadata / bundled notice |
|---|---|
| Microsoft.Extensions.AI, Abstractions, OpenAI 10.10.0 | MIT |
| Microsoft.ML.OnnxRuntime and Managed 1.23.2 | Bundled MIT `LICENSE` / `LICENSE.txt`, Microsoft copyright |
| Tokenizers.HuggingFace 3.23.1 | Apache-2.0 |
| System.Numerics.Tensors 10.0.9 / resolved runtime 10.0.12 | MIT |
| Microsoft.Data.Sqlite 10.0.12 | MIT |
| SQLitePCLRaw bundle/core/provider/native library 2.1.12 | Apache-2.0; bundled SQLite native distribution has its own notices |
| OpenAI .NET SDK (transitive) | MIT |
| xUnit v3 / Visual Studio runner | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |

Restore retains package-carried license files/notices. Review the complete
resolved transitive dependency graph and native notices before redistributing a
published binary; this table is not a legal certification or a substitute for
them. No model assets or dependency binaries are checked into this repository.
The owner-authorized source MIT license does not relicense any of these
independent model/dependency components.

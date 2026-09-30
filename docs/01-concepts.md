# 1. Model routing, without the jargon

[Learning path](README.md) | Next: [offline walkthrough](02-offline-walkthrough.md)

## Why choose a route at all?

Imagine an application with several chat models available. Some may be cheaper
or faster for short transformations; others may handle harder work better.
Always using the same expensive configuration can waste resources. Always using
the cheapest configuration can fail tasks it cannot handle.

A **model router** chooses which configured executor gets a request. It does
not solve the task by itself. Neither model names nor a "strong" label prove
that a route is sufficient: you must measure the actual cost, latency, and task
outcomes in your environment.

This sample uses stable tier names **fast**, **balanced**, and **strong**. The
application maps them to concrete model/client configurations. Those names express
intent, not prices, benchmarks, or guarantees. Replacing a model is a configuration
change, not permission to reuse that old model's outcome evidence.

## Two kinds of model, two different jobs

| Component | Input | Output | Job in this sample |
|---|---|---|---|
| Chat model | Messages and options | A response, possibly streamed | Execute the sorting task |
| Julia decision model | State, question, described alternatives | A selected candidate and relative probabilities | Recommend a minimum sufficient tier |

Julia-1 is a pretrained, multilingual, mmBERT-small-derived finite-choice model
with about 144.3M parameters. It compares alternatives supplied by the caller.
It does **not** generate the sorted array, supply missing facts, or become a
general chat assistant. The unrelated Julia programming language is not required.

Descriptions carry meaning. Candidate IDs such as `fast` are stable lookup keys;
the model is asked to score descriptions like "Routine, clear, short
transformations." Changing descriptions can change predictions, even if IDs stay
the same.

The default demo substitutes a **fixture** for both jobs. It always recommends
Fast with a known distribution and supplies scripted chat outputs. That isolates
the pipeline's behavior from model variability. Native mode substitutes the real
Julia provider only; live mode substitutes real downstream chat as well.

## Decision is not policy

A decision signal says, "Of these choices, this seems most appropriate."
Policy says, "Given our constraints, these routes are allowed."

For example:

```text
model recommends Fast
  + application requires at least Balanced
  = policy selects Balanced

model recommends Fast
  + recent independently known Fast failure in a comparable cohort/config
  = policy selects at least Balanced
```

The raw recommendation and distribution remain visible even when policy changes
the selection. This avoids pretending the model itself recommended the override.
A model cannot grant capabilities or override an application's minimum tier.

Probabilities describe a distribution **over the supplied alternatives**. A value
of `0.9` for Fast does not mean a 90% chance that the downstream model will solve
the task. A sharp or near-one-hot distribution is not certainty, calibration, or
evidence of route quality. The sample's confidence floor is an illustrative
application setting, not a scientifically fitted success threshold.

## Completion is not correctness

For the task "sort `[3,1,2]`", returning `[3,1,2]` can be a completely successful
HTTP request and a completely wrong task result. Keep two questions separate:

| Question | Answered by |
|---|---|
| Did the provider invocation complete? | MEAI's attempt telemetry |
| Did the returned result meet independent task checks? | Application/verifier feedback |

The exact-sort verifier computes the expected `[1,2,3]` from the input, then
checks the response. It does not ask the selected model to judge its own answer.
Without supported verification or explicit application feedback, quality remains
**Unknown**, not Success.

Known completed feedback is persisted and included in a later decision's compact
state. A deterministic recent-failure rule also enforces a minimum tier. That
makes outcome awareness behavioral, not just extra text in a prompt.

This changes **future requests**, not Julia's weights. It is not online training,
reinforcement learning, a bandit, or automatic optimal routing.

## Where MEAI and ML.NET fit

`Microsoft.Extensions.AI` (**MEAI**) provides .NET interfaces such as
`IChatClient` and routing implementations such as `RoutingChatClient` and
`FailoverChatClient`. They let application code work with different providers
through consistent request/response shapes.

**ML.NET** uses `Microsoft.ML`, including training/data APIs such as `MLContext`
and `IDataView`. This sample does not train an ML.NET classifier or use those
APIs. Julia's ONNX graph is executed by **ONNX Runtime (ORT)**, a separate runtime;
its tokenizer converts text into the model's token IDs.

`IDecisionGenerator` belongs to the vendored decision abstraction, not to released
MEAI. "Decision client" describes a concept; there is no invented MEAI
`IDecisionClient` here.

## Glossary

| Term | Plain-language meaning |
|---|---|
| Model | Trained parameters and inference behavior; not the whole application |
| Provider | The service/library that exposes or runs a model |
| Client | The .NET object used to invoke a provider; chat uses `IChatClient` |
| Route | A named, concrete endpoint/model/options configuration with a declared tier and capabilities |
| Tier | Stable application label for intended capability, independent of model catalog names |
| Decision signal | Selected candidate plus its original candidate-relative distribution |
| Policy | Deterministic application rules that constrain allowed execution |
| Attempt | One invocation of a selected chat route |
| Failover | Retry an eligible alternate after a pre-output provider failure |
| Verifier | Independently defined checks of a completed task result |
| Outcome | Success, Failure, or Unknown task quality, distinct from transport status |
| Provenance | Whether known feedback came from a verifier or the application |
| Cohort | Application-owned category/version defining comparable tasks |
| Evidence | Bounded relevant known outcomes from completed runs on current configurations |

**Predict before continuing:** if a provider completes with a bad array, should
MEAI automatically retry? No: that is quality failure, not transport failure.
The [next page](02-offline-walkthrough.md) shows exactly when a stronger route is
chosen.

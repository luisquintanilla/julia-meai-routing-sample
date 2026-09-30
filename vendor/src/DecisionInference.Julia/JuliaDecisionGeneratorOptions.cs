using Microsoft.ML.OnnxRuntime;

namespace DecisionInference.Julia;

/// <summary>Local construction settings, not per-call decision generation options.</summary>
public sealed class JuliaDecisionGeneratorOptions
{
    public required string ModelPath { get; init; }
    public required string TokenizerJsonPath { get; init; }

    /// <summary>Configures temporary options once before loading. Do not retain or dispose them.</summary>
    public Action<SessionOptions>? ConfigureSession { get; init; }
}

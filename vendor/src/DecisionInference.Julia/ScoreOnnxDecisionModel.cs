using DecisionInference.Onnx;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace DecisionInference.Julia;

/// <summary>Julia's logits-only ORT stage. Native buffers are released before returning managed tensors.</summary>
public sealed class ScoreOnnxDecisionModel : IDisposable
{
    private readonly MarkerOnnxRunner _runner;

    public ScoreOnnxDecisionModel(InferenceSession session, bool ownsSession = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!MarkerOnnxRunner.HasMarkerInputs(session) ||
            !session.OutputMetadata.Keys.SequenceEqual(["logits"]))
            throw new ArgumentException("Julia requires exactly five named inputs and only the logits output.");
        MarkerOnnxRunner.ValidateInputs(session, "Julia");
        MarkerOnnxRunner.ValidateMetadata("logits", session.OutputMetadata["logits"], typeof(float), 2, "Julia");
        _runner = new(session, ownsSession, "Julia");
    }

    public static ScoreOnnxDecisionModel Load(string modelPath, SessionOptions? sessionOptions = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("An explicit local Julia model is required.", modelPath);
        InferenceSession session;
        if (sessionOptions is null) { using var defaults = new SessionOptions(); session = new(modelPath, defaults); }
        else session = new(modelPath, sessionOptions);
        try { return new(session, ownsSession: true); }
        catch { session.Dispose(); throw; }
    }

    public Tensor<float> Score(JuliaInputBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var outputs = _runner.Run(new(batch.BatchSize, batch.SequenceLength, batch.MarkerWidth,
            batch.Ids, batch.Attention, batch.Positions, batch.Masks, batch.Types),
            [new("logits", batch.MarkerWidth, "Julia logits")], this, cancellationToken, outputs =>
        {
            var logits = outputs[0];
            for (int row = 0; row < batch.BatchSize; row++)
                for (int i = 0; i < batch.OptionCounts[row]; i++)
                    if (!float.IsFinite(logits[row, i])) throw new DecisionProtocolException("Nonfinite Julia valid-option logit.");
        });
        return outputs[0];
    }

    public void Dispose() => _runner.Dispose();
}

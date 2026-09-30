using System.Numerics.Tensors;

namespace DecisionInference.Julia;

/// <summary>Owned uncalibrated Julia evidence; no display suppression or action head.</summary>
public sealed class JuliaRawAnswer
{
    public IReadOnlyList<double> Logits { get; }
    internal JuliaRawAnswer(double[] logits) => Logits = Array.AsReadOnly(logits);
}

public sealed class DecodeDecisions
{
    public DecisionResult Decode(JuliaInputBatch inputs, Tensor<float> logits)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(logits);
        if (!logits.Lengths.SequenceEqual(new nint[] { inputs.BatchSize, inputs.MarkerWidth }))
            throw new DecisionProtocolException("Julia input/output shapes disagree.");
        var answers = new List<DecisionAnswer>();
        for (int row = 0; row < inputs.BatchSize; row++)
        {
            var q = inputs.Input.Questions[row];
            double[] values = new double[inputs.OptionCounts[row]];
            for (int i = 0; i < values.Length; i++) values[i] = logits[row, i];
            if (values.Any(v => !double.IsFinite(v))) throw new DecisionProtocolException("Nonfinite Julia valid-option logit.");
            double[] probabilities = new double[values.Length];
            TensorPrimitives.Subtract<double>(values, TensorPrimitives.Max<double>(values), probabilities);
            TensorPrimitives.Exp<double>(probabilities, probabilities);
            TensorPrimitives.Divide<double>(probabilities, TensorPrimitives.Sum<double>(probabilities), probabilities);
            DecisionAnswer answer = q switch
            {
                BinaryDecisionQuestion => new BinaryDecisionAnswer(q.Id, probabilities[1]),
                ChoiceDecisionQuestion c => new ChoiceDecisionAnswer(q.Id, c.Candidates[Array.IndexOf(probabilities, probabilities.Max())].Id,
                    c.Candidates.Select((c, i) => KeyValuePair.Create(c.Id, probabilities[i]))),
                ScoreDecisionQuestion => new ScoreDecisionAnswer(q.Id, probabilities),
                _ => throw new NotSupportedException()
            };
            answer.RawRepresentation = new JuliaRawAnswer(values);
            answers.Add(answer);
        }
        return new(inputs.Input, JuliaDecisionGenerator.ModelId, answers);
    }
}

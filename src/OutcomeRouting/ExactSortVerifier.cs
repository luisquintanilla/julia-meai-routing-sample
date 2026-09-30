using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

/// <summary>Checks independent input-derived expectations, never another model's judgment.</summary>
public sealed class ExactSortVerifier : ITaskVerifier
{
    private readonly int[] _expected;

    public ExactSortVerifier(IEnumerable<int> input)
    {
        _expected = input.Order().ToArray();
    }

    public Feedback Verify(ChatResponse response)
    {
        if (response.Messages.Count != 1 ||
            response.Messages[0].Role != ChatRole.Assistant ||
            response.Messages[0].Contents.Any(content => content is not TextContent))
        {
            return Feedback.Unknown("unsupported-response");
        }

        try
        {
            using var json = JsonDocument.Parse(response.Text);

            bool matches = json.RootElement.ValueKind == JsonValueKind.Array &&
                json.RootElement.GetArrayLength() == _expected.Length &&
                json.RootElement.EnumerateArray()
                    .Select(element => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number)
                        ? (int?)number
                        : null)
                    .SequenceEqual(_expected.Select(number => (int?)number));

            return new Feedback(matches ? Outcome.Success : Outcome.Failure, Provenance.Verifier, "exact-sort-v1");
        }
        catch (JsonException)
        {
            return new Feedback(Outcome.Failure, Provenance.Verifier, "exact-sort-v1");
        }
    }
}

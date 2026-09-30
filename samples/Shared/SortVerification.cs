using Microsoft.Extensions.AI;
using OutcomeRouting;

namespace RoutingSamples;

internal static class SortVerification
{
    private const string Task = "Sort [3,1,2] ascending. Return only the JSON array.";

    internal static ITaskVerifier? ForMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 1 &&
            messages[0].Role == ChatRole.User &&
            messages[0].Text == Task)
        {
            return new ExactSortVerifier([3, 1, 2]);
        }

        // This check knows this input, not arbitrary sorting tasks or general model quality.
        return null;
    }
}

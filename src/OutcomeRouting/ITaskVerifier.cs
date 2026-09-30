using Microsoft.Extensions.AI;

namespace OutcomeRouting;

public interface ITaskVerifier
{
    Feedback Verify(ChatResponse response);
}

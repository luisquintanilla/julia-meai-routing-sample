using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RoutingSamples;

/// <summary>A deterministic stand-in for a configured model. Never makes network calls.</summary>
internal sealed class DemoChatClient(string model, string response) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new(
        providerName: "demonstration",
        providerUri: new Uri("fixture://local"),
        defaultModelId: model);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;

        yield return new ChatResponseUpdate(ChatRole.Assistant, response);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata : null;
    }

    public void Dispose()
    {
    }
}

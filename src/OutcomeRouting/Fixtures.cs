using System.Runtime.CompilerServices;
using DecisionInference;
using Microsoft.Extensions.AI;

namespace OutcomeRouting;

/// <summary>SIMULATED decision signal, not Julia inference or an accuracy measurement.</summary>
public sealed class FixtureDecisionGenerator : IDecisionGenerator
{
    private readonly Tier _selected;
    private readonly double[] _probabilities;
    public FixtureDecisionGenerator(Tier selected = Tier.Fast, double fast = 0.9, double balanced = 0.08, double strong = 0.02)
        => (_selected, _probabilities) = (selected, [fast, balanced, strong]);

    public Task<DecisionResult> GenerateAsync(DecisionInput input, DecisionGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.ModelId is not null || options?.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("The fixture supports no model overrides or extension options.");
        return Task.FromResult(new DecisionResult(input, "fixture-not-julia",
            [new ChoiceDecisionAnswer(DecisionPolicy.QuestionId, DecisionPolicy.Id(_selected),
                Enum.GetValues<Tier>().Select((t, i) => KeyValuePair.Create(DecisionPolicy.Id(t), _probabilities[i]))) ]));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

public enum FixtureBehavior { Correct, Incorrect, FailBeforeOutput, FailAfterOutput, Empty }

/// <summary>SIMULATED downstream response and provider failure; no network model is invoked.</summary>
public sealed class ScriptedChatClient(FixtureBehavior behavior) : IChatClient
{
    private int _invocations;
    public int Invocations => _invocations;
    public ChatOptions? ObservedOptions { get; private set; }
    public IReadOnlyList<ChatMessage>? ObservedMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Observe(messages, options, cancellationToken);
        if (behavior == FixtureBehavior.FailBeforeOutput) throw new HttpRequestException("Simulated pre-output provider failure.");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Text())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Observe(messages, options, cancellationToken);
        await Task.CompletedTask;
        if (behavior == FixtureBehavior.FailBeforeOutput) throw new HttpRequestException("Simulated pre-output provider failure.");
        if (behavior == FixtureBehavior.Empty) yield break;
        yield return new(ChatRole.Assistant, Text());
        cancellationToken.ThrowIfCancellationRequested();
        if (behavior == FixtureBehavior.FailAfterOutput) throw new HttpRequestException("Simulated failure after committed output.");
    }

    private string Text() => behavior switch
    {
        FixtureBehavior.Incorrect => "[3,1,2]",
        FixtureBehavior.Empty => "",
        _ => "[1,2,3]"
    };

    private void Observe(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _invocations);
        ObservedOptions = options;
        ObservedMessages = messages.ToArray();
    }
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// One model a provider serves, and which of the optional request parameters it accepts.
/// </summary>
/// <remarks>
/// These belong to the model rather than the provider because a provider serves several and the
/// model is the user's to choose: Opus 5 takes a mid-conversation system entry and a fallback
/// policy, Sonnet 5 rejects both with a 400, and the tier a turn runs on says nothing about which
/// of them was picked.
/// </remarks>
internal sealed record AssistantModel
{
    public required string Id { get; init; }

    /// <summary>Whether <c>{"role": "system"}</c> is accepted part-way through the message list, which
    /// is what keeps the live repo-state block out of the cached prefix.</summary>
    public bool MidConversationSystem { get; init; }

    /// <summary>Whether a policy decline can be re-served rather than surfaced as a dead turn.</summary>
    public bool ServerSideFallbacks { get; init; }

    /// <summary>Newer OpenAI models reject <c>max_tokens</c> and take <c>max_completion_tokens</c>.</summary>
    public bool UsesMaxCompletionTokens { get; init; }

    /// <summary>The <c>reasoning_effort</c> a request offering tools has to state, or null where the
    /// parameter means nothing here. OpenAI's current models reason by default and then refuse
    /// function tools on <c>/v1/chat/completions</c>, so leaving it unsaid is what fails; naming the
    /// opt-out is what makes a toolset usable at all.</summary>
    public string? ToolReasoningEffort { get; init; }

    /// <summary>What a model this build does not list is taken to accept. Every capability here is an
    /// opt-in the request works without, so a model typed by hand, served by a local endpoint, or
    /// released after this build states none of them rather than guessing at one.</summary>
    public static AssistantModel Unlisted { get; } = new() { Id = string.Empty };
}

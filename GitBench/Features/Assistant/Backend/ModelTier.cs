namespace GitBench.Features.Assistant.Backend;

/// Which model a turn runs on. Not user-visible: the chat/agent loop is Chat, contextual
/// one-shot actions are Quick. Which model each tier maps to is the provider's to say, and what
/// that model accepts is <see cref="AssistantModel"/>'s — the tier itself grants nothing.
internal enum ModelTier
{
    Chat,
    Quick,
}

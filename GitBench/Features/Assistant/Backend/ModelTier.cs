namespace GitBench.Features.Assistant.Backend;

/// Which model a turn runs on. Not user-visible: the chat/agent loop is Chat, contextual
/// one-shot actions are Quick. Which model each tier maps to is the provider's to say.
internal enum ModelTier
{
    Chat,
    Quick,
}

internal static class ModelTiers
{
    // Mid-conversation {"role": "system"} entries are an Opus 5 feature; Haiku 4.5 rejects them and
    // no OpenAI-compatible endpoint has an equivalent, so those tiers carry live repo state in the
    // user turn instead.
    public static bool SupportsMidConversationSystem(this AssistantProvider provider, ModelTier tier) =>
        provider.MidConversationSystem && tier == ModelTier.Chat;

    // Server-side refusal fallbacks are only offered for the frontier models the chat tier uses.
    public static bool SupportsServerSideFallbacks(this AssistantProvider provider, ModelTier tier) =>
        provider.ServerSideFallbacks && tier == ModelTier.Chat;
}

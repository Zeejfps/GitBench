using System.Diagnostics;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// One model provider the assistant can be pointed at: where it lives, how it is hosted, which
/// model it answers with unless told otherwise, and what its wire format supports.
/// </summary>
internal sealed record AssistantProvider
{
    public required string Id { get; init; }

    /// <summary>The provider's own name, shown as-is — a proper noun, so it is not localized.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Everything up to but not including the endpoint path, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    public required AssistantWireFormat Wire { get; init; }

    public required AssistantHosting Hosting { get; init; }

    /// <summary>The model a role runs on when none is chosen for it. Names an entry in the hosted
    /// model list rather than standing beside it, so it cannot drift out of the list the picker
    /// offers.</summary>
    public required string DefaultModel { get; init; }

    /// <summary>The cheaper model the commit message runs on when none is chosen for it — the one
    /// job small and frequent enough not to want the frontier model. Same rule about the list.</summary>
    public required string QuickModel { get; init; }

    public string DefaultModelFor(AssistantRole role) => role switch
    {
        AssistantRole.General or AssistantRole.Review or AssistantRole.Walkthrough => DefaultModel,
        AssistantRole.CommitMessage => QuickModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>The name this provider's key is kept under in the OS secret store.</summary>
    public string SecretName => Id + "-api-key";

    /// <summary>Whether a turn cannot be attempted without a key. A self-hosted endpoint answers
    /// unauthenticated, but a gateway put in front of one is routinely behind a token, so a key
    /// given for it is still sent.</summary>
    public bool RequiresApiKey => Hosting is AssistantHosting.Hosted;

    /// <summary>The models offered as a starting point — a default and never a whitelist, so a model
    /// typed by hand is taken as typed. Empty where the served models are the user's own.</summary>
    public IReadOnlyList<string> ModelPresets => Hosting switch
    {
        AssistantHosting.Hosted hosted => [.. hosted.Models.Select(m => m.Id)],
        AssistantHosting.SelfHosted => [],
        _ => throw new UnreachableException(),
    };

    /// <summary>What this build knows the named model accepts, or <see cref="AssistantModel.Unlisted"/>
    /// for one it has never heard of — which is every model a self-hosted endpoint serves.</summary>
    public AssistantModel Capabilities(string model) => Hosting switch
    {
        AssistantHosting.Hosted hosted =>
            hosted.Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
            ?? AssistantModel.Unlisted,
        AssistantHosting.SelfHosted => AssistantModel.Unlisted,
        _ => throw new UnreachableException(),
    };
}

/// <summary>
/// The providers the assistant knows how to reach. One wire format serves all but the first; what
/// differs between them is data, which is what this list holds.
/// </summary>
internal static class AssistantProviders
{
    public const string AnthropicId = "anthropic";

    public static AssistantProvider Anthropic { get; } = new()
    {
        Id = AnthropicId,
        DisplayName = "Anthropic",
        BaseUrl = "https://api.anthropic.com/v1",
        Wire = AssistantWireFormat.Anthropic,
        DefaultModel = "claude-opus-5",
        QuickModel = "claude-haiku-4-5-20251001",
        // Mid-conversation system entries and a server-side fallback policy are the frontier models'
        // alone. Sonnet 5 and Haiku 4.5 reject both by name, so they say so here rather than
        // inheriting an Anthropic-wide yes.
        Hosting = new AssistantHosting.Hosted("ANTHROPIC_API_KEY",
        [
            new() { Id = "claude-opus-5", MidConversationSystem = true, ServerSideFallbacks = true },
            new() { Id = "claude-haiku-4-5-20251001" },
            new() { Id = "claude-sonnet-5" },
            new() { Id = "claude-fable-5", MidConversationSystem = true, ServerSideFallbacks = true },
        ]),
        MaxOutputTokens = AssistantTurn.DefaultMaxTokens,
    };

    public static AssistantProvider OpenAi { get; } = new()
    {
        Id = "openai",
        DisplayName = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        DefaultModel = "gpt-5.6-sol",
        QuickModel = "gpt-5.6-luna",
        Hosting = new AssistantHosting.Hosted("OPENAI_API_KEY",
        [
            new() { Id = "gpt-5.6-sol", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
            new() { Id = "gpt-5.6-luna", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
            new() { Id = "gpt-5.6-terra", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
        ]),
        MaxOutputTokens = 32000,
    };

    public static AssistantProvider OpenRouter { get; } = new()
    {
        Id = "openrouter",
        DisplayName = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        DefaultModel = "openai/gpt-5.6-sol",
        QuickModel = "openai/gpt-5.6-luna",
        // The gateway normalizes the request shape across the models it fronts, so none of them
        // needs the per-model parameters their first-party endpoints do.
        Hosting = new AssistantHosting.Hosted("OPENROUTER_API_KEY",
        [
            new() { Id = "openai/gpt-5.6-sol" },
            new() { Id = "openai/gpt-5.6-luna" },
            new() { Id = "openai/gpt-5.6-terra" },
            new() { Id = "anthropic/claude-opus-5" },
            new() { Id = "anthropic/claude-sonnet-5" },
            new() { Id = "google/gemini-3.6-flash" },
        ]),
        MaxOutputTokens = 16000,
    };

    public static AssistantProvider Groq { get; } = new()
    {
        Id = "groq",
        DisplayName = "Groq",
        BaseUrl = "https://api.groq.com/openai/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        DefaultModel = "openai/gpt-oss-120b",
        QuickModel = "openai/gpt-oss-20b",
        Hosting = new AssistantHosting.Hosted("GROQ_API_KEY",
        [
            new() { Id = "openai/gpt-oss-120b" },
            new() { Id = "openai/gpt-oss-20b" },
            new() { Id = "groq/compound" },
            new() { Id = "groq/compound-mini" },
        ]),
    };

    public static AssistantProvider Together { get; } = new()
    {
        Id = "together",
        DisplayName = "Together",
        BaseUrl = "https://api.together.xyz/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        DefaultModel = "deepseek-ai/DeepSeek-V4-Pro",
        QuickModel = "openai/gpt-oss-20b",
        Hosting = new AssistantHosting.Hosted("TOGETHER_API_KEY",
        [
            new() { Id = "deepseek-ai/DeepSeek-V4-Pro" },
            new() { Id = "openai/gpt-oss-20b" },
            new() { Id = "moonshotai/Kimi-K3" },
            new() { Id = "zai-org/GLM-5.2" },
            new() { Id = "openai/gpt-oss-120b" },
            new() { Id = "meta-llama/Llama-3.3-70B-Instruct-Turbo" },
        ]),
    };

    public static AssistantProvider Ollama { get; } = new()
    {
        Id = "ollama",
        DisplayName = "Ollama",
        BaseUrl = "http://localhost:11434/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        Hosting = new AssistantHosting.SelfHosted(),
        DefaultModel = "gpt-oss:20b",
        QuickModel = "gpt-oss:20b",
        MaxOutputTokens = 4096,
    };

    public static AssistantProvider LmStudio { get; } = new()
    {
        Id = "lmstudio",
        DisplayName = "LM Studio",
        BaseUrl = "http://localhost:1234/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        Hosting = new AssistantHosting.SelfHosted(),
        DefaultModel = "local-model",
        QuickModel = "local-model",
        MaxOutputTokens = 4096,
    };

    public static AssistantProvider VLlm { get; } = new()
    {
        Id = "vllm",
        DisplayName = "vLLM",
        BaseUrl = "http://localhost:8000/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        Hosting = new AssistantHosting.SelfHosted(),
        DefaultModel = "local-model",
        QuickModel = "local-model",
        MaxOutputTokens = 4096,
    };

    public static IReadOnlyList<AssistantProvider> All { get; } =
        [Anthropic, OpenAi, OpenRouter, Groq, Together, Ollama, LmStudio, VLlm];

    public static AssistantProvider Default => Anthropic;

    /// <summary>The provider with this id, or null when this build has none — for a stored id that
    /// must not silently stand for another provider.</summary>
    public static AssistantProvider? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The provider with this id, or the default one — an id from a hand-edited or
    /// newer preferences file resolves rather than throwing.</summary>
    public static AssistantProvider Resolve(string? id) => Find(id) ?? Default;
}

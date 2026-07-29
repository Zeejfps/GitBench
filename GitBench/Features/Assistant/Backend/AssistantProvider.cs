namespace GitBench.Features.Assistant.Backend;

/// Which request shape a provider speaks: the Messages API, or the <c>/v1/chat/completions</c> shape
/// OpenAI, Ollama, LM Studio, OpenRouter, Groq, Together and vLLM all share.
internal enum AssistantWireFormat
{
    Anthropic,
    OpenAiCompatible,
}

/// <summary>
/// One model provider the assistant can be pointed at: where it lives, whether it has to be signed,
/// which model answers each tier, and what its wire format supports.
/// </summary>
internal sealed record AssistantProvider
{
    public required string Id { get; init; }

    /// <summary>The provider's own name, shown as-is — a proper noun, so it is not localized.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Everything up to but not including the endpoint path, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    public required AssistantWireFormat Wire { get; init; }

    /// <summary>Whether a turn cannot be attempted without a key. Stated by every provider rather
    /// than defaulted, so a new hosted one cannot arrive silently keyless.</summary>
    public required bool RequiresApiKey { get; init; }

    public required string ChatModel { get; init; }

    public required string QuickModel { get; init; }

    /// <summary>Every model this build knows the provider serves, in the order the picker offers them.
    /// The tier defaults name entries here rather than standing beside them, so the models the tiers
    /// run on cannot drift out of the list. Empty where the endpoint serves whatever the user has
    /// loaded rather than a published catalogue.</summary>
    public IReadOnlyList<AssistantModel> Models { get; init; } = [];

    /// <summary>The variable read when no key is saved, or null for a provider with no such convention.</summary>
    public string? EnvironmentVariable { get; init; }

    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>Whether the endpoint is the user's to set — true for the self-hosted providers, whose
    /// address is a local port rather than a product.</summary>
    public bool CustomBaseUrl { get; init; }

    /// <summary>False where tool calling depends on whichever model happens to be loaded, so a turn
    /// that never calls a tool is worth reporting rather than trusting.</summary>
    public bool ToolCalling { get; init; } = true;

    /// <summary>Whether a key is worth asking for — which is not the same question as whether one is
    /// needed. A self-hosted endpoint answers unauthenticated, but its address is the user's to set
    /// and a gateway put in front of it is routinely behind a token, so a key given for one is sent.
    /// Without this the only field left open for that token is the endpoint, which is unmasked and
    /// kept in plain text.</summary>
    public bool AcceptsApiKey => RequiresApiKey || CustomBaseUrl;

    /// <summary>The name this provider's key is kept under in the OS secret store.</summary>
    public string SecretName => Id + "-api-key";

    /// <summary>False where the endpoint serves whatever the user has loaded rather than a published
    /// catalogue, so any list would be a guess and the model stays free text.</summary>
    public bool KnownModels => Models.Count > 0;

    /// <summary>The models offered as a starting point — a default and never a whitelist, so a model
    /// typed by hand is taken as typed. Empty where the served models are the user's own.</summary>
    public IReadOnlyList<string> ModelPresets => [.. Models.Select(m => m.Id)];

    /// <summary>What this build knows the named model accepts, or <see cref="AssistantModel.Unlisted"/>
    /// for one it has never heard of.</summary>
    public AssistantModel Capabilities(string model) =>
        Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
        ?? AssistantModel.Unlisted;

    public string ModelFor(ModelTier tier) => tier == ModelTier.Quick ? QuickModel : ChatModel;
}

/// <summary>
/// The providers the assistant knows how to reach. One OpenAI-compatible implementation serves all
/// but the first; what differs between them is data, which is what this list holds.
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
        RequiresApiKey = true,
        EnvironmentVariable = "ANTHROPIC_API_KEY",
        ChatModel = "claude-opus-5",
        QuickModel = "claude-haiku-4-5-20251001",
        // Mid-conversation system entries and a server-side fallback policy are the frontier models'
        // alone. Sonnet 5 and Haiku 4.5 reject both by name, so they say so here rather than
        // inheriting an Anthropic-wide yes.
        Models =
        [
            new() { Id = "claude-opus-5", MidConversationSystem = true, ServerSideFallbacks = true },
            new() { Id = "claude-haiku-4-5-20251001" },
            new() { Id = "claude-sonnet-5" },
            new() { Id = "claude-fable-5", MidConversationSystem = true, ServerSideFallbacks = true },
        ],
        MaxOutputTokens = AssistantTurn.DefaultMaxTokens,
    };

    public static AssistantProvider OpenAi { get; } = new()
    {
        Id = "openai",
        DisplayName = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = true,
        EnvironmentVariable = "OPENAI_API_KEY",
        ChatModel = "gpt-5.6-sol",
        QuickModel = "gpt-5.6-luna",
        Models =
        [
            new() { Id = "gpt-5.6-sol", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
            new() { Id = "gpt-5.6-luna", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
            new() { Id = "gpt-5.6-terra", UsesMaxCompletionTokens = true, ToolReasoningEffort = "none" },
        ],
        MaxOutputTokens = 32000,
    };

    public static AssistantProvider OpenRouter { get; } = new()
    {
        Id = "openrouter",
        DisplayName = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = true,
        EnvironmentVariable = "OPENROUTER_API_KEY",
        ChatModel = "openai/gpt-5.6-sol",
        QuickModel = "openai/gpt-5.6-luna",
        // The gateway normalizes the request shape across the models it fronts, so none of them
        // needs the per-model parameters their first-party endpoints do.
        Models =
        [
            new() { Id = "openai/gpt-5.6-sol" },
            new() { Id = "openai/gpt-5.6-luna" },
            new() { Id = "openai/gpt-5.6-terra" },
            new() { Id = "anthropic/claude-opus-5" },
            new() { Id = "anthropic/claude-sonnet-5" },
            new() { Id = "google/gemini-3.6-flash" },
        ],
        MaxOutputTokens = 16000,
    };

    public static AssistantProvider Groq { get; } = new()
    {
        Id = "groq",
        DisplayName = "Groq",
        BaseUrl = "https://api.groq.com/openai/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = true,
        EnvironmentVariable = "GROQ_API_KEY",
        ChatModel = "openai/gpt-oss-120b",
        QuickModel = "openai/gpt-oss-20b",
        Models =
        [
            new() { Id = "openai/gpt-oss-120b" },
            new() { Id = "openai/gpt-oss-20b" },
            new() { Id = "groq/compound" },
            new() { Id = "groq/compound-mini" },
        ],
    };

    public static AssistantProvider Together { get; } = new()
    {
        Id = "together",
        DisplayName = "Together",
        BaseUrl = "https://api.together.xyz/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = true,
        EnvironmentVariable = "TOGETHER_API_KEY",
        ChatModel = "deepseek-ai/DeepSeek-V4-Pro",
        QuickModel = "openai/gpt-oss-20b",
        Models =
        [
            new() { Id = "deepseek-ai/DeepSeek-V4-Pro" },
            new() { Id = "openai/gpt-oss-20b" },
            new() { Id = "moonshotai/Kimi-K3" },
            new() { Id = "zai-org/GLM-5.2" },
            new() { Id = "openai/gpt-oss-120b" },
            new() { Id = "meta-llama/Llama-3.3-70B-Instruct-Turbo" },
        ],
    };

    public static AssistantProvider Ollama { get; } = new()
    {
        Id = "ollama",
        DisplayName = "Ollama",
        BaseUrl = "http://localhost:11434/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = false,
        ChatModel = "gpt-oss:20b",
        QuickModel = "gpt-oss:20b",
        MaxOutputTokens = 4096,
        CustomBaseUrl = true,
        ToolCalling = false,
    };

    public static AssistantProvider LmStudio { get; } = new()
    {
        Id = "lmstudio",
        DisplayName = "LM Studio",
        BaseUrl = "http://localhost:1234/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = false,
        ChatModel = "local-model",
        QuickModel = "local-model",
        MaxOutputTokens = 4096,
        CustomBaseUrl = true,
        ToolCalling = false,
    };

    public static AssistantProvider VLlm { get; } = new()
    {
        Id = "vllm",
        DisplayName = "vLLM",
        BaseUrl = "http://localhost:8000/v1",
        Wire = AssistantWireFormat.OpenAiCompatible,
        RequiresApiKey = false,
        ChatModel = "local-model",
        QuickModel = "local-model",
        MaxOutputTokens = 4096,
        CustomBaseUrl = true,
        ToolCalling = false,
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

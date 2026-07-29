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

    /// <summary>Models offered besides the two the tiers already name. The picker's list is this
    /// table widened rather than a second one, so the tier defaults cannot drift out of it.</summary>
    public IReadOnlyList<string> OtherModels { get; init; } = [];

    /// <summary>False where the endpoint serves whatever the user has loaded rather than a published
    /// catalogue, so any list would be a guess and the model stays free text.</summary>
    public bool KnownModels { get; init; } = true;

    /// <summary>The variable read when no key is saved, or null for a provider with no such convention.</summary>
    public string? EnvironmentVariable { get; init; }

    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>Whether <c>{"role": "system"}</c> is accepted part-way through the message list, which
    /// is what keeps the live repo-state block out of the cached prefix.</summary>
    public bool MidConversationSystem { get; init; }

    /// <summary>Whether a policy decline can be re-served rather than surfaced as a dead turn.</summary>
    public bool ServerSideFallbacks { get; init; }

    /// <summary>Whether the endpoint is the user's to set — true for the self-hosted providers, whose
    /// address is a local port rather than a product.</summary>
    public bool CustomBaseUrl { get; init; }

    /// <summary>Newer OpenAI models reject <c>max_tokens</c> and take <c>max_completion_tokens</c>.</summary>
    public bool UsesMaxCompletionTokens { get; init; }

    /// <summary>The <c>reasoning_effort</c> a request offering tools has to state, or null where the
    /// parameter means nothing here. OpenAI's current models reason by default and then refuse
    /// function tools on <c>/v1/chat/completions</c>, so leaving it unsaid is what fails; naming the
    /// opt-out is what makes a toolset usable at all.</summary>
    public string? ToolReasoningEffort { get; init; }

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

    /// <summary>The models offered as a starting point — a default and never a whitelist, so a model
    /// typed by hand is taken as typed. Empty where the served models are the user's own.</summary>
    public IReadOnlyList<string> ModelPresets => KnownModels
        ? new[] { ChatModel, QuickModel }.Concat(OtherModels).Distinct(StringComparer.Ordinal).ToArray()
        : [];

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
        OtherModels = ["claude-sonnet-5", "claude-fable-5"],
        MaxOutputTokens = AssistantTurn.DefaultMaxTokens,
        MidConversationSystem = true,
        ServerSideFallbacks = true,
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
        OtherModels = ["gpt-5.6-terra"],
        MaxOutputTokens = 32000,
        UsesMaxCompletionTokens = true,
        ToolReasoningEffort = "none",
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
        OtherModels =
        [
            "openai/gpt-5.6-terra",
            "anthropic/claude-opus-5",
            "anthropic/claude-sonnet-5",
            "google/gemini-3.6-flash",
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
        OtherModels = ["groq/compound", "groq/compound-mini"],
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
        OtherModels =
        [
            "moonshotai/Kimi-K3",
            "zai-org/GLM-5.2",
            "openai/gpt-oss-120b",
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
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
        KnownModels = false,
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
        KnownModels = false,
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
        KnownModels = false,
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

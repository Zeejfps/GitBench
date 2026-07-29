using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using ZGF.Gui.Desktop;
using Xunit;

namespace GitBench.Tests;

// The provider registry and what credentials mean per provider: where a key comes from, and the fact
// that for a local endpoint "no key" is a configured state rather than a gap.
public sealed class AssistantProviderTests
{
    [Fact]
    public void AnthropicKeepsTheSecretNameItWasAlreadyStoredUnder()
    {
        Assert.Equal("anthropic-api-key", AssistantProviders.Anthropic.SecretName);
        Assert.Equal("openai-api-key", AssistantProviders.OpenAi.SecretName);
        Assert.Distinct(AssistantProviders.All.Select(p => p.SecretName));
    }

    [Fact]
    public void EveryProviderDeclaresAWireFormatAndModelsForBothTiers()
    {
        foreach (var provider in AssistantProviders.All)
        {
            Assert.NotEmpty(provider.ModelFor(ModelTier.Chat));
            Assert.NotEmpty(provider.ModelFor(ModelTier.Quick));
            Assert.StartsWith("http", provider.BaseUrl);
            Assert.True(provider.MaxOutputTokens > 0);
        }

        // The two capabilities the Anthropic writer branches on are Anthropic's alone.
        foreach (var model in AssistantProviders.All
                     .Where(p => p.Wire == AssistantWireFormat.OpenAiCompatible)
                     .SelectMany(p => p.Models))
        {
            Assert.False(model.MidConversationSystem);
            Assert.False(model.ServerSideFallbacks);
        }
    }

    // The bug this replaced: capabilities hung off the provider and the tier, so a user who picked
    // Sonnet 5 as their chat model still got `fallbacks` and a mid-conversation system entry — both
    // of which that model rejects outright.
    [Fact]
    public void CapabilitiesFollowTheModelRatherThanTheTierItAnswers()
    {
        var anthropic = AssistantProviders.Anthropic;

        foreach (var id in new[] { "claude-opus-5", "claude-fable-5" })
        {
            Assert.True(anthropic.Capabilities(id).MidConversationSystem);
            Assert.True(anthropic.Capabilities(id).ServerSideFallbacks);
        }

        foreach (var id in new[] { "claude-sonnet-5", "claude-haiku-4-5-20251001" })
        {
            Assert.False(anthropic.Capabilities(id).MidConversationSystem);
            Assert.False(anthropic.Capabilities(id).ServerSideFallbacks);
        }

        // Choosing one for the chat tier is what used to send the parameters it rejects.
        var sonnet = AssistantSettings.For(anthropic.Id, "claude-sonnet-5").Connect("sk-ant");
        Assert.False(sonnet.Capabilities(ModelTier.Chat).ServerSideFallbacks);
        Assert.False(sonnet.Capabilities(ModelTier.Chat).MidConversationSystem);
    }

    // A model this build has never seen states nothing optional: every capability is an opt-in the
    // request works without, so guessing costs a rejected turn and abstaining costs nothing.
    [Fact]
    public void AModelTheBuildDoesNotListClaimsNoOptionalParameters()
    {
        var unlisted = AssistantProviders.Anthropic.Capabilities("claude-opus-9-released-next-year");

        Assert.False(unlisted.MidConversationSystem);
        Assert.False(unlisted.ServerSideFallbacks);
        Assert.False(unlisted.UsesMaxCompletionTokens);
        Assert.Null(unlisted.ToolReasoningEffort);

        // Which is also every model a local endpoint serves, since the user pulled them, not us.
        Assert.Empty(AssistantProviders.Ollama.Models);
        Assert.Null(AssistantProviders.Ollama.Capabilities("qwen3:8b").ToolReasoningEffort);

        // A listed model matches however the user cased it.
        Assert.True(AssistantProviders.Anthropic.Capabilities("Claude-Opus-5").ServerSideFallbacks);
    }

    // The presets are the tier table widened rather than a second list, so the models the tiers run
    // on cannot fall out of the list the picker offers.
    [Fact]
    public void ThePresetsAlwaysContainTheModelsTheTiersRunOn()
    {
        foreach (var provider in AssistantProviders.All.Where(p => p.KnownModels))
        {
            Assert.Contains(provider.ChatModel, provider.ModelPresets);
            Assert.Contains(provider.QuickModel, provider.ModelPresets);
            Assert.Distinct(provider.ModelPresets);
        }

        // A local endpoint serves whatever the user pulled, so there is nothing to offer and the
        // model field stays free text.
        foreach (var provider in AssistantProviders.All.Where(p => p.CustomBaseUrl))
        {
            Assert.False(provider.KnownModels);
            Assert.Empty(provider.ModelPresets);
        }
    }

    // "Needs a key" and "takes a key" are different questions. Answering only the first is what left
    // a self-hosted gateway's token with nowhere to go but the endpoint field.
    [Fact]
    public void EveryProviderTakesAKeyAndOnlyTheHostedOnesDemandOne()
    {
        foreach (var provider in AssistantProviders.All)
        {
            Assert.True(provider.AcceptsApiKey);
            // A provider that demands one obviously takes one; the reverse does not follow.
            if (provider.RequiresApiKey) Assert.True(provider.AcceptsApiKey);
        }

        foreach (var provider in AssistantProviders.All.Where(p => p.CustomBaseUrl))
            Assert.False(provider.RequiresApiKey);

        foreach (var provider in AssistantProviders.All.Where(p => !p.CustomBaseUrl))
        {
            Assert.True(provider.RequiresApiKey);
            Assert.NotNull(provider.EnvironmentVariable);
        }
    }

    // A key given for a local endpoint reaches the connection, which is what puts it on the wire.
    [Fact]
    public void AKeyGivenToASelfHostedProviderIsCarriedAndDoesNotChangeWhetherItIsUsable()
    {
        var secrets = new MemorySecretStore();
        var credentials = new AssistantCredentials(secrets);

        Assert.True(credentials.Save(AssistantProviders.Ollama, "gateway-token"));
        Assert.Equal("gateway-token", credentials.ApiKeyFor(AssistantProviders.Ollama));
        Assert.Equal(AssistantKeySource.Saved, credentials.SourceFor(AssistantProviders.Ollama));
        Assert.Null(credentials.SavedFor(AssistantProviders.LmStudio));

        var connection = AssistantSettings
            .For(AssistantProviders.Ollama.Id, baseUrl: "https://gw.internal/v1")
            .Connect(credentials.ApiKeyFor(AssistantProviders.Ollama));
        Assert.Equal("gateway-token", connection.ApiKey);
        Assert.True(connection.IsUsable);

        // And taking it away leaves the provider exactly as usable as it was before.
        credentials.Clear(AssistantProviders.Ollama);
        Assert.Equal(AssistantKeySource.NotRequired, credentials.SourceFor(AssistantProviders.Ollama));
        Assert.True(credentials.StateFor(AssistantProviders.Ollama).IsUsable);
    }

    [Fact]
    public void AnUnknownProviderIdResolvesToTheDefaultRatherThanThrowing()
    {
        Assert.Equal(AssistantProviders.Anthropic, AssistantProviders.Resolve("some-provider-from-the-future"));
        Assert.Equal(AssistantProviders.Anthropic, AssistantProviders.Resolve(null));
        Assert.Equal(AssistantProviders.OpenAi, AssistantProviders.Resolve("OpenAI"));
    }

    [Fact]
    public void ASavedKeyOutranksTheEnvironmentAndIsKeptPerProvider()
    {
        var secrets = new MemorySecretStore();
        var credentials = new AssistantCredentials(secrets);
        credentials.Save(AssistantProviders.OpenAi, "sk-openai");

        Assert.Equal("sk-openai", credentials.ApiKeyFor(AssistantProviders.OpenAi));
        Assert.Equal(AssistantKeySource.Saved, credentials.SourceFor(AssistantProviders.OpenAi));
        // The provider next door is untouched by it.
        Assert.Null(credentials.SavedFor(AssistantProviders.Anthropic));
    }

    // The environment variable is a fallback the app reads and never owns, so clearing a saved key
    // can still leave one in effect.
    [Fact]
    public void ClearingASavedKeyFallsBackToTheProvidersEnvironmentVariable()
    {
        var secrets = new MemorySecretStore();
        var credentials = new AssistantCredentials(secrets);
        var variable = AssistantProviders.OpenAi.EnvironmentVariable!;
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "sk-from-env");
        try
        {
            credentials.Save(AssistantProviders.OpenAi, "sk-saved");
            Assert.Equal("sk-saved", credentials.ApiKeyFor(AssistantProviders.OpenAi));

            credentials.Clear(AssistantProviders.OpenAi);
            Assert.Equal("sk-from-env", credentials.ApiKeyFor(AssistantProviders.OpenAi));
            Assert.Equal(AssistantKeySource.Environment, credentials.SourceFor(AssistantProviders.OpenAi));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void AProviderThatNeedsNoKeyIsConfiguredWithoutOne()
    {
        var credentials = new AssistantCredentials(new MemorySecretStore());

        Assert.Null(credentials.ApiKeyFor(AssistantProviders.Ollama));
        Assert.Equal(AssistantKeySource.NotRequired, credentials.SourceFor(AssistantProviders.Ollama));
        Assert.True(AssistantConnection.For(AssistantProviders.Ollama).IsUsable);
        Assert.False(AssistantConnection.For(AssistantProviders.OpenAi).IsUsable);
        Assert.True(AssistantConnection.For(AssistantProviders.OpenAi, apiKey: "sk-test").IsUsable);
    }

    [Fact]
    public void SettingsCarryTheOverridesAndTheEndpointIsBuiltFromThem()
    {
        var settings = AssistantSettings.For("ollama", "  qwen  ", "  http://box:11434/v1/  ");
        var connection = settings.Connect(null);

        Assert.Equal(AssistantProviders.Ollama, settings.Provider);
        Assert.Equal("qwen", connection.ChatModel);
        Assert.Equal("http://box:11434/v1/chat/completions", connection.Endpoint("/chat/completions"));
        Assert.Equal(
            "https://api.anthropic.com/v1/messages",
            AssistantConnection.Default.Endpoint("/messages"));

        // Blank overrides read as "the provider's own", not as an empty model name.
        var bare = AssistantSettings.For("openai", "   ", "");
        Assert.Null(bare.Model);
        Assert.Null(bare.BaseUrl);
        Assert.Equal(AssistantProviders.OpenAi.ChatModel, bare.Connect(null).ChatModel);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public string? Get(string name) => _secrets.GetValueOrDefault(name);

        public bool Set(string name, string secret)
        {
            _secrets[name] = secret;
            return true;
        }

        public bool Delete(string name) => _secrets.Remove(name);
    }
}

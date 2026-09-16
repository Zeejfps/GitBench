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
    public void EveryProviderDeclaresAWireFormatAndADefaultModel()
    {
        foreach (var provider in AssistantProviders.All)
        {
            Assert.NotEmpty(provider.DefaultModel);
            Assert.NotEmpty(provider.QuickModel);
            Assert.StartsWith("http", provider.BaseUrl);
            Assert.True(provider.MaxOutputTokens > 0);
        }

        // The two capabilities the Anthropic writer branches on are Anthropic's alone.
        foreach (var model in AssistantProviders.All
                     .Where(p => p.Wire == AssistantWireFormat.OpenAiCompatible)
                     .Select(p => p.Hosting)
                     .OfType<AssistantHosting.Hosted>()
                     .SelectMany(h => h.Models))
        {
            Assert.False(model.MidConversationSystem);
            Assert.False(model.ServerSideFallbacks);
        }
    }

    // The bug this replaced: capabilities hung off the provider, so a user who picked Sonnet 5 as
    // their chat model still got `fallbacks` and a mid-conversation system entry — both of which
    // that model rejects outright.
    [Fact]
    public void CapabilitiesFollowTheModelRatherThanTheProvider()
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

        // Choosing one for the chat is what used to send the parameters it rejects.
        var sonnet = AssistantConnection.For(anthropic, "claude-sonnet-5", apiKey: "sk-ant");
        Assert.False(sonnet.Capabilities.ServerSideFallbacks);
        Assert.False(sonnet.Capabilities.MidConversationSystem);
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
        Assert.IsType<AssistantHosting.SelfHosted>(AssistantProviders.Ollama.Hosting);
        Assert.Null(AssistantProviders.Ollama.Capabilities("qwen3:8b").ToolReasoningEffort);

        // A listed model matches however the user cased it.
        Assert.True(AssistantProviders.Anthropic.Capabilities("Claude-Opus-5").ServerSideFallbacks);
    }

    // The presets are the model table widened rather than a second list, so the defaults cannot fall
    // out of the list the picker offers.
    [Fact]
    public void ThePresetsAlwaysContainTheDefaultModels()
    {
        foreach (var provider in AssistantProviders.All.Where(p => p.Hosting is AssistantHosting.Hosted))
        {
            Assert.Contains(provider.DefaultModel, provider.ModelPresets);
            Assert.Contains(provider.QuickModel, provider.ModelPresets);
            Assert.Distinct(provider.ModelPresets);
        }

        // A local endpoint serves whatever the user pulled, so there is nothing to offer and the
        // model field stays free text.
        foreach (var provider in AssistantProviders.All.Where(p => p.Hosting is AssistantHosting.SelfHosted))
            Assert.Empty(provider.ModelPresets);
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
            .Connect(AssistantRole.General, credentials.Keyring());
        Assert.Equal("gateway-token", connection.ApiKey);
        Assert.Equal("https://gw.internal/v1", connection.BaseUrl);
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
        var variable = Assert.IsType<AssistantHosting.Hosted>(AssistantProviders.OpenAi.Hosting).EnvironmentVariable;
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
        var connection = settings.Connect(AssistantRole.Review, AssistantKeyring.Empty);

        Assert.Equal(AssistantProviders.Ollama, settings.ModelFor(AssistantRole.Review).Provider);
        Assert.Equal("qwen", connection.Model);
        Assert.Equal("http://box:11434/v1/chat/completions", connection.Endpoint("/chat/completions"));
        Assert.Equal(
            "https://api.anthropic.com/v1/messages",
            AssistantConnection.Default.Endpoint("/messages"));

        // Blank overrides read as "the provider's own", not as an empty model name.
        var bare = AssistantSettings.For("openai", "   ", "");
        Assert.Null(bare.ModelFor(AssistantRole.General).Model);
        Assert.Null(bare.BaseUrlFor(AssistantProviders.OpenAi));
        Assert.Empty(bare.BaseUrls);
        Assert.Equal(AssistantProviders.OpenAi.DefaultModel, bare.Connect(AssistantRole.General, AssistantKeyring.Empty).Model);
    }

    // Each role's connection is its own: provider, endpoint, model and the key that signs it all
    // follow the role, so three roles on three providers are three keys on the wire.
    [Fact]
    public void EachRoleConnectsThroughItsOwnProviderWithThatProvidersKey()
    {
        var credentials = new AssistantCredentials(new MemorySecretStore());
        credentials.Save(AssistantProviders.Anthropic, "sk-ant");
        credentials.Save(AssistantProviders.OpenAi, "sk-openai");

        var settings = AssistantSettings.Default
            .WithModel(AssistantRole.General, AssistantProviders.Anthropic.Id, "claude-sonnet-5")
            .WithModel(AssistantRole.Review, AssistantProviders.OpenAi.Id, null)
            .WithModel(AssistantRole.Walkthrough, AssistantProviders.Ollama.Id, "qwen3:8b")
            .WithBaseUrl(AssistantProviders.Ollama.Id, "http://box:11434/v1");
        var keys = credentials.Keyring();

        var chat = settings.Connect(AssistantRole.General, keys);
        Assert.Equal(("anthropic", "claude-sonnet-5", "sk-ant"), (chat.Provider.Id, chat.Model, chat.ApiKey));

        var review = settings.Connect(AssistantRole.Review, keys);
        Assert.Equal(("openai", "gpt-5.6-sol", "sk-openai"), (review.Provider.Id, review.Model, review.ApiKey));

        var walkthrough = settings.Connect(AssistantRole.Walkthrough, keys);
        Assert.Equal(("ollama", "qwen3:8b", (string?)null), (walkthrough.Provider.Id, walkthrough.Model, walkthrough.ApiKey));
        Assert.Equal("http://box:11434/v1", walkthrough.BaseUrl);
        Assert.True(walkthrough.IsUsable);

        // The commit message defaults to the cheaper model of whichever provider it is on.
        var commit = settings.Connect(AssistantRole.CommitMessage, keys);
        Assert.Equal(("anthropic", "claude-haiku-4-5-20251001", "sk-ant"), (commit.Provider.Id, commit.Model, commit.ApiKey));

        // A role on a provider with no key is the one that cannot send, and only that one.
        var groq = settings.WithModel(AssistantRole.Review, AssistantProviders.Groq.Id, null);
        Assert.False(groq.Connect(AssistantRole.Review, keys).IsUsable);
        Assert.True(groq.Connect(AssistantRole.General, keys).IsUsable);
    }

    // What was persisted is what comes back, and what this build does not know is dropped rather
    // than remapped onto the default provider.
    [Fact]
    public void SettingsRebuildFromWhatWasPersistedAndDropWhatTheBuildDoesNotKnow()
    {
        var settings = AssistantSettings.From(
            models:
            [
                ("general", "openai", "gpt-5.6-terra"),
                ("review", "provider-from-the-future", "whatever"),
                ("role-from-the-future", "anthropic", null),
                ("walkthrough", "Together", null),
            ],
            baseUrls: [("ollama", "http://box:11434/v1"), ("provider-from-the-future", "http://x")]);

        Assert.Equal(("openai", "gpt-5.6-terra"), Pair(settings.ModelFor(AssistantRole.General)));
        Assert.Equal(AssistantModelChoice.Default, settings.ModelFor(AssistantRole.Review));
        Assert.Equal(("together", (string?)null), Pair(settings.ModelFor(AssistantRole.Walkthrough)));
        Assert.Equal("http://box:11434/v1", settings.BaseUrlFor(AssistantProviders.Ollama));
        Assert.Single(settings.BaseUrls);

        // And the pair goes round: what Models and BaseUrls hand out rebuilds the same settings.
        var again = AssistantSettings.From(
            settings.Models.Select(m => (AssistantRoles.Id(m.Role), m.Choice.Provider.Id, m.Choice.Model)),
            settings.BaseUrls.Select(b => (b.Key, (string?)b.Value)));
        Assert.Equal(settings, again);
    }

    private static (string, string?) Pair(AssistantModelChoice choice) => (choice.Provider.Id, choice.Model);

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

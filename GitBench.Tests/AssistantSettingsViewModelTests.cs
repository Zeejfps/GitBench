using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// What the connection card is driven by: which provider is being edited, what the fields start at
// when it changes, and what reaches the store on save.
public sealed class AssistantSettingsViewModelTests : IDisposable
{
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeAssistantSessionStore _store = new();
    private readonly AssistantViewModel _vm;

    public AssistantSettingsViewModelTests()
    {
        _vm = new AssistantViewModel(_store, _loc, new MessageBus());
    }

    // Nothing configured is the onboarding case, and it is the same card either way.
    [Fact]
    public void TheCardIsUpUntilAConnectionResolves()
    {
        Assert.True(_vm.ShowSettings.Value);
        Assert.True(_vm.NeedsSetup.Value);
        Assert.False(_vm.CloseSettings.CanExecute.Value);

        _store.SetConfigured(true);
        Assert.False(_vm.ShowSettings.Value);

        _vm.OpenSettings.Execute();
        Assert.True(_vm.ShowSettings.Value);
        Assert.True(_vm.CloseSettings.CanExecute.Value);

        _vm.CloseSettings.Execute();
        Assert.False(_vm.ShowSettings.Value);
    }

    [Fact]
    public void PickingAProviderStartsFromItsOwnDefaultsRatherThanTheLastOnes()
    {
        _vm.ModelDraft.Value = "claude-opus-5";
        _vm.KeyDraft.Value = "sk-anthropic";

        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);

        Assert.Equal(string.Empty, _vm.ModelDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);
        Assert.Equal(AssistantProviders.Ollama.ChatModel, _vm.ModelHint.Value);
        Assert.Equal(AssistantProviders.Ollama.BaseUrl, _vm.BaseUrlHint.Value);
        // A local endpoint is the user's to point at, and takes a key without needing one.
        Assert.True(_vm.WantsBaseUrl.Value);
        Assert.True(_vm.WantsApiKey.Value);
        Assert.True(_vm.IsApiKeyOptional.Value);
        Assert.Equal(
            "Ollama needs no API key. Add one only if your endpoint sits behind a gateway that asks for one.",
            _vm.KeyHint.Value);
    }

    // The trap this replaced: the field was hidden wherever a key was not required, so the only box
    // left open for a gateway token was the endpoint — unmasked, and kept in plain text on disk.
    [Fact]
    public void ASelfHostedProviderOffersAKeyFieldAndSaysItIsOptional()
    {
        foreach (var provider in AssistantProviders.All.Where(p => p.CustomBaseUrl))
        {
            _vm.SetProviderDraft(provider.Id);

            Assert.True(_vm.WantsApiKey.Value);
            Assert.True(_vm.IsApiKeyOptional.Value);
        }

        // And a provider that demands one asks for it outright rather than offering it.
        _vm.SetProviderDraft(AssistantProviders.OpenAi.Id);
        Assert.True(_vm.WantsApiKey.Value);
        Assert.False(_vm.IsApiKeyOptional.Value);
    }

    // A gateway token typed for a local endpoint is that provider's, and is written to its own slot.
    [Fact]
    public void AKeyTypedForASelfHostedProviderIsSavedUnderIt()
    {
        _vm.OpenSettings.Execute();
        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);
        _vm.BaseUrlDraft.Value = "https://gw.internal/v1";
        _vm.KeyDraft.Value = "gateway-token";

        _vm.SaveSettings.Execute();

        var write = Assert.Single(_store.Writes);
        Assert.Equal(AssistantProviders.Ollama.Id, write.ProviderId);
        Assert.Equal("gateway-token", write.ApiKey);
        Assert.Equal("gateway-token", _store.KeyStateFor(AssistantProviders.Ollama).SavedKey);
        // The endpoint is no longer the only place a token could go, so it carries none.
        Assert.Equal("https://gw.internal/v1", _store.Saved!.BaseUrl);
        Assert.Null(_store.KeyStateFor(AssistantProviders.LmStudio).SavedKey);

        // Once it is stored the card holds it, and the line of prose has nothing left to add.
        _vm.OpenSettings.Execute();
        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);
        Assert.Equal("gateway-token", _vm.KeyDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // The non-regression that matters most: offering the field must not turn "no key" into a gap.
    [Fact]
    public void ASelfHostedProviderWithNoKeyIsStillFullyConfigured()
    {
        _store.Save(AssistantSettings.For(AssistantProviders.Ollama.Id), apiKey: null);

        Assert.True(_store.IsConfigured.Value);
        Assert.False(_vm.NeedsSetup.Value);
        Assert.True(_store.KeyStateFor(AssistantProviders.Ollama).IsUsable);
        Assert.Contains(
            "Ollama", _vm.BuildProviderSwitcher().Where(i => !i.IsSeparator).Select(i => i.Label));
        // And the provider list still says nothing is outstanding for it.
        Assert.Null(_vm.BuildProviderMenu().Single(i => i.Label == "Ollama").Shortcut);
    }

    [Fact]
    public void SavingSendsTheWholeConnectionAndForgetsTheKeyItTypedIn()
    {
        _vm.SetProviderDraft(AssistantProviders.OpenAi.Id);
        _vm.ModelDraft.Value = "gpt-5.6-luna";
        _vm.KeyDraft.Value = "sk-openai";

        _vm.SaveSettings.Execute();

        Assert.Equal(AssistantProviders.OpenAi.Id, _store.Saved!.ProviderId);
        Assert.Equal("gpt-5.6-luna", _store.Saved.Model);
        Assert.Null(_store.Saved.BaseUrl);
        Assert.Equal("sk-openai", _store.SavedApiKey);
    }

    // The key can come from somewhere the app never wrote, and the card has to say so rather than
    // asking for one it already has.
    [Fact]
    public void TheKeyHintSaysWhereTheKeyInEffectCameFrom()
    {
        // A saved key is in the field rather than described, so there is no line left to read.
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        Assert.Equal(string.Empty, _vm.KeyHint.Value);

        _store.SetSavedKey(AssistantProviders.Anthropic, null);
        _store.SetEnvironmentKey(AssistantProviders.Anthropic, "sk-from-env");
        Assert.Equal("Using ANTHROPIC_API_KEY from the environment.", _vm.KeyHint.Value);

        _store.SetEnvironmentKey(AssistantProviders.Anthropic, null);
        Assert.Equal("No key yet.", _vm.KeyHint.Value);
    }

    // The card answers for whichever provider is being edited, not only the one in use: masking
    // leaves no other way to tell a provider that is already set up from one that is not.
    [Fact]
    public void TheKeyHintAnswersForTheProviderBeingEdited()
    {
        _store.SetSavedKey(AssistantProviders.Groq, "gsk-stored");

        Assert.Equal("No key yet.", _vm.KeyHint.Value);

        _vm.SetProviderDraft(AssistantProviders.Groq.Id);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // The same state, in the list the card picks a provider from — so the choice is made knowing
    // which providers are answered for.
    [Fact]
    public void TheProviderMenuSaysWhatEachProviderHasForAKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _store.SetEnvironmentKey(AssistantProviders.OpenAi, "sk-from-env");

        var items = _vm.BuildProviderMenu().ToDictionary(i => i.Label, i => i.Shortcut);

        Assert.Equal("Key saved", items["Anthropic"]);
        Assert.Equal("From environment", items["OpenAI"]);
        Assert.Equal("No key", items["Groq"]);
        Assert.Null(items["Ollama"]);
    }

    // The field holds the stored key rather than a sentence about it. It is masked, and the
    // framework refuses the clipboard over a masked field, so it reads as bullets and nothing else.
    [Fact]
    public void TheCardOpensHoldingTheSavedKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");

        _vm.OpenSettings.Execute();

        Assert.Equal("sk-stored", _vm.KeyDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // A key the app only reads is not the app's to hand back or to take away, so the box stays empty
    // and the line that explains where it comes from stays put.
    [Fact]
    public void AKeyFromTheEnvironmentIsDescribedRatherThanFilledIn()
    {
        _store.SetEnvironmentKey(AssistantProviders.Anthropic, "sk-from-env");

        _vm.OpenSettings.Execute();
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);
        Assert.Equal("Using ANTHROPIC_API_KEY from the environment.", _vm.KeyHint.Value);

        // Saving without touching it leaves the stored key alone rather than reading as a deletion.
        _vm.SaveSettings.Execute();
        Assert.Null(_store.SavedApiKey);
    }

    // Emptying the box only means "forget it" where the box was holding the stored key to begin with.
    [Fact]
    public void EmptyingAFilledKeyFieldForgetsTheStoredKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _vm.OpenSettings.Execute();

        _vm.KeyDraft.Value = string.Empty;
        _vm.SaveSettings.Execute();
        Assert.Equal(string.Empty, _store.SavedApiKey);

        // And once it is gone, an empty box is just an empty box again.
        _vm.OpenSettings.Execute();
        _vm.SaveSettings.Execute();
        Assert.Null(_store.SavedApiKey);
    }

    // Another provider's key is not read on the way past, so switching away empties the box — and
    // switching back must not then read as a deletion.
    [Fact]
    public void SwitchingProviderAndBackKeepsTheStoredKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _vm.OpenSettings.Execute();

        _vm.SetProviderDraft(AssistantProviders.Groq.Id);
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);

        _vm.SetProviderDraft(AssistantProviders.Anthropic.Id);
        Assert.Equal("sk-stored", _vm.KeyDraft.Value);
    }

    // A default and not a whitelist: the menu fills the field in, and the field keeps whatever is
    // typed instead.
    [Fact]
    public void TheModelMenuOffersTheProvidersOwnAndMarksTheOneInTheField()
    {
        _vm.SetProviderDraft(AssistantProviders.OpenAi.Id);
        Assert.True(_vm.HasModelPresets.Value);

        var items = _vm.BuildModelMenu();
        Assert.Equal(AssistantProviders.OpenAi.ModelPresets, items.Select(i => i.Label));
        Assert.DoesNotContain(items, i => i.Checked);

        items.First(i => i.Label == AssistantProviders.OpenAi.QuickModel).OnSelected();
        Assert.Equal(AssistantProviders.OpenAi.QuickModel, _vm.ModelDraft.Value);
        Assert.Single(_vm.BuildModelMenu().Where(i => i.Checked));

        // A model that is not on the list is kept as typed rather than rejected.
        _vm.ModelDraft.Value = "gpt-from-next-year";
        _vm.SaveSettings.Execute();
        Assert.Equal("gpt-from-next-year", _store.Saved!.Model);
        Assert.DoesNotContain(_vm.BuildModelMenu(), i => i.Checked);
    }

    // A local endpoint serves whatever the user pulled, so there is nothing honest to offer.
    [Fact]
    public void ALocalProviderHasNoModelListToOffer()
    {
        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);

        Assert.False(_vm.HasModelPresets.Value);
        Assert.Empty(_vm.BuildModelMenu());
    }

    [Fact]
    public void TheProviderMenuMarksTheOneBeingEdited()
    {
        _vm.SetProviderDraft(AssistantProviders.LmStudio.Id);

        var items = _vm.BuildProviderMenu();
        Assert.Equal(AssistantProviders.All.Count, items.Count);
        var checkedItem = Assert.Single(items.Where(i => i.Checked));
        Assert.Equal("LM Studio", checkedItem.Label);

        items.First(i => i.Label == "Groq").OnSelected();
        Assert.Equal(AssistantProviders.Groq.Id, _vm.ProviderDraft.Value);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
        _loc.Dispose();
    }
}

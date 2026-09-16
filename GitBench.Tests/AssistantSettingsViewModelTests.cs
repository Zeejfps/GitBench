using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Settings;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// What the settings card is driven by: which provider's key is being edited, what the fields start
// at when it changes, what each role's line offers, and what reaches the store on save.
public sealed class AssistantSettingsViewModelTests : IDisposable
{
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeAssistantSessionStore _store = new();
    private readonly MessageBus _bus = new();
    private readonly AssistantViewModel _vm;

    public AssistantSettingsViewModelTests()
    {
        _vm = new AssistantViewModel(_store, _loc, _bus);
    }

    private AssistantRoleDraft Chat => _vm.RoleDraft(AssistantRole.General);
    private AssistantRoleDraft Review => _vm.RoleDraft(AssistantRole.Review);
    private AssistantRoleDraft Walkthrough => _vm.RoleDraft(AssistantRole.Walkthrough);

    // Nothing configured is the onboarding case: the card takes the composer's place until the chat
    // can send. Afterwards the same card lives in the settings window, which the gear opens on its page.
    [Fact]
    public void TheCardIsUpUntilTheChatsConnectionResolvesAndTheGearOpensTheSettingsWindowAfter()
    {
        Assert.True(_vm.ShowSettings.Value);
        Assert.True(_vm.NeedsSetup.Value);

        _store.SetConfigured(true);
        Assert.False(_vm.ShowSettings.Value);

        var opened = new List<OpenSettingsWindowMessage>();
        _bus.Subscribe<OpenSettingsWindowMessage>(opened.Add);
        _vm.OpenSettings.Execute();
        Assert.Equal(SettingsPage.Agent, Assert.Single(opened).Page);
        Assert.False(_vm.ShowSettings.Value);
    }

    [Fact]
    public void PickingAProviderForItsKeyStartsFromItsOwnDefaultsRatherThanTheLastOnes()
    {
        _vm.KeyDraft.Value = "sk-anthropic";

        _vm.SetKeyProviderDraft(AssistantProviders.Ollama.Id);

        Assert.Equal(string.Empty, _vm.KeyDraft.Value);
        Assert.Equal(AssistantProviders.Ollama.BaseUrl, _vm.BaseUrlHint.Value);
        // A local endpoint is the user's to point at, and takes a key without needing one.
        Assert.True(_vm.WantsBaseUrl.Value);
        Assert.True(_vm.IsApiKeyOptional.Value);
        Assert.Equal(
            "Ollama needs no API key. Add one only if your endpoint sits behind a gateway that asks for one.",
            _vm.KeyHint.Value);
    }

    // The trap this replaced: the field was hidden wherever a key was not required, so the only box
    // left open for a gateway token was the endpoint — unmasked, and kept in plain text on disk.
    [Fact]
    public void ASelfHostedProviderSaysItsKeyIsOptional()
    {
        foreach (var provider in AssistantProviders.All.Where(p => p.Hosting is AssistantHosting.SelfHosted))
        {
            _vm.SetKeyProviderDraft(provider.Id);

            Assert.True(_vm.IsApiKeyOptional.Value);
        }

        // And a provider that demands one asks for it outright rather than offering it.
        _vm.SetKeyProviderDraft(AssistantProviders.OpenAi.Id);
        Assert.False(_vm.IsApiKeyOptional.Value);
    }

    // A gateway token typed for a local endpoint is that provider's, and is written to its own slot.
    [Fact]
    public void AKeyTypedForASelfHostedProviderIsSavedUnderIt()
    {
        _vm.ResetSettings.Execute();
        _vm.SetKeyProviderDraft(AssistantProviders.Ollama.Id);
        _vm.BaseUrlDraft.Value = "https://gw.internal/v1";
        _vm.KeyDraft.Value = "gateway-token";

        _vm.SaveSettings.Execute();

        var write = Assert.IsType<AssistantKeyEdit.Store>(Assert.Single(_store.Writes));
        Assert.Equal(AssistantProviders.Ollama.Id, write.Provider.Id);
        Assert.Equal("gateway-token", write.Key);
        Assert.Equal("gateway-token", _store.KeyStateFor(AssistantProviders.Ollama).SavedKey);
        // The endpoint is no longer the only place a token could go, so it carries none.
        Assert.Equal("https://gw.internal/v1", _store.Saved!.BaseUrlFor(AssistantProviders.Ollama));
        Assert.Null(_store.KeyStateFor(AssistantProviders.LmStudio).SavedKey);

        // Once it is stored the card holds it, and the line of prose has nothing left to add.
        _vm.ResetSettings.Execute();
        _vm.SetKeyProviderDraft(AssistantProviders.Ollama.Id);
        Assert.Equal("gateway-token", _vm.KeyDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // The non-regression that matters most: offering the field must not turn "no key" into a gap.
    [Fact]
    public void ASelfHostedProviderWithNoKeyIsStillFullyConfigured()
    {
        _store.Save(AssistantSettings.For(AssistantProviders.Ollama.Id), AssistantKeyEdit.None);

        foreach (var role in AssistantRoles.All)
            Assert.True(_store.IsConfigured(role).Value);
        Assert.False(_vm.NeedsSetup.Value);
        Assert.True(_store.KeyStateFor(AssistantProviders.Ollama).IsUsable);
        Assert.Contains(
            "Ollama", _vm.BuildProviderSwitcher().Where(i => !i.IsSeparator).Select(i => i.Label));
        // And the provider list still says nothing is outstanding for it.
        Assert.Null(_vm.BuildProviderMenu().Single(i => i.Label == "Ollama").Shortcut);
    }

    // The first key given sets up every role, not one line of three: nothing else could answer.
    [Fact]
    public void TheFirstProviderSetUpBecomesEveryRolesProvider()
    {
        _vm.SetKeyProviderDraft(AssistantProviders.OpenAi.Id);
        foreach (var role in AssistantRoles.All)
            Assert.Equal(AssistantProviders.OpenAi.Id, _vm.RoleDraft(role).ProviderId.Value);

        _vm.KeyDraft.Value = "sk-openai";
        _vm.SaveSettings.Execute();

        foreach (var role in AssistantRoles.All)
            Assert.Equal(AssistantProviders.OpenAi.Id, _store.Saved!.ModelFor(role).Provider.Id);
        Assert.Equal("sk-openai", Assert.IsType<AssistantKeyEdit.Store>(_store.SavedKey).Key);
        Assert.False(_vm.NeedsSetup.Value);
    }

    // A role already on a provider that answers is nobody's to move.
    [Fact]
    public void ARoleOnAProviderWithAKeyStaysWhereItIsWhenAnotherIsSetUp()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _vm.ResetSettings.Execute();
        Review.SetProvider(AssistantProviders.Groq.Id);

        _vm.SetKeyProviderDraft(AssistantProviders.OpenAi.Id);

        Assert.Equal(AssistantProviders.Anthropic.Id, Chat.ProviderId.Value);
        Assert.Equal(AssistantProviders.OpenAi.Id, Review.ProviderId.Value);
        Assert.Equal(AssistantProviders.Anthropic.Id, Walkthrough.ProviderId.Value);
    }

    // The roles are independent lines: each is saved as its own provider and model, and any provider
    // with a key can serve any of them.
    [Fact]
    public void EachRoleIsSavedWithItsOwnProviderAndModel()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _store.SetSavedKey(AssistantProviders.OpenAi, "sk-openai");
        _vm.ResetSettings.Execute();

        Chat.Model.Value = "claude-sonnet-5";
        Review.SetProvider(AssistantProviders.OpenAi.Id);
        Review.Model.Value = "gpt-5.6-terra";
        Walkthrough.SetProvider(AssistantProviders.OpenAi.Id);

        _vm.SaveSettings.Execute();

        var saved = _store.Saved!;
        Assert.Equal(("anthropic", "claude-sonnet-5"), Pair(saved.ModelFor(AssistantRole.General)));
        Assert.Equal(("openai", "gpt-5.6-terra"), Pair(saved.ModelFor(AssistantRole.Review)));
        Assert.Equal(("openai", (string?)null), Pair(saved.ModelFor(AssistantRole.Walkthrough)));
        Assert.Equal("gpt-5.6-sol", saved.EffectiveModelFor(AssistantRole.Walkthrough));
        Assert.IsType<AssistantKeyEdit.Keep>(_store.SavedKey);

        foreach (var role in AssistantRoles.All)
            Assert.True(_store.IsConfigured(role).Value);
    }

    // A model name means nothing to another provider, so moving a role's provider clears its model.
    [Fact]
    public void MovingARolesProviderClearsItsModel()
    {
        Chat.Model.Value = "claude-sonnet-5";

        Chat.SetProvider(AssistantProviders.Groq.Id);

        Assert.Equal(string.Empty, Chat.Model.Value);
        Assert.Equal(AssistantProviders.Groq.DefaultModel, Chat.ModelHint.Value);
    }

    // The commit message's line hints at the cheaper model, which is what it runs on unless told.
    [Fact]
    public void TheCommitMessageLineDefaultsToTheProvidersQuickModel()
    {
        var commit = _vm.RoleDraft(AssistantRole.CommitMessage);

        Assert.Equal(AssistantProviders.Anthropic.QuickModel, commit.ModelHint.Value);
        Assert.Equal(AssistantProviders.Anthropic.DefaultModel, Chat.ModelHint.Value);

        commit.SetProvider(AssistantProviders.OpenAi.Id);
        Assert.Equal(AssistantProviders.OpenAi.QuickModel, commit.ModelHint.Value);
    }

    // An endpoint belongs to its provider whichever roles use it, and is not lost by moving the
    // key picker on before saving.
    [Fact]
    public void AnEndpointTypedForOneProviderSurvivesEditingAnothersKey()
    {
        _vm.ResetSettings.Execute();
        _vm.SetKeyProviderDraft(AssistantProviders.Ollama.Id);
        _vm.BaseUrlDraft.Value = "http://box:11434/v1";

        _vm.SetKeyProviderDraft(AssistantProviders.LmStudio.Id);
        _vm.BaseUrlDraft.Value = "http://box:1234/v1";
        _vm.SaveSettings.Execute();

        Assert.Equal("http://box:11434/v1", _store.Saved!.BaseUrlFor(AssistantProviders.Ollama));
        Assert.Equal("http://box:1234/v1", _store.Saved.BaseUrlFor(AssistantProviders.LmStudio));
        Assert.Null(_store.Saved.BaseUrlFor(AssistantProviders.VLlm));
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

    // The card answers for whichever provider is being edited, not only the ones in use: masking
    // leaves no other way to tell a provider that is already set up from one that is not.
    [Fact]
    public void TheKeyHintAnswersForTheProviderBeingEdited()
    {
        _store.SetSavedKey(AssistantProviders.Groq, "gsk-stored");

        Assert.Equal("No key yet.", _vm.KeyHint.Value);

        _vm.SetKeyProviderDraft(AssistantProviders.Groq.Id);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // The same state, in the lists a provider is picked from — the key picker's and each role's —
    // so the choice is made knowing which providers are answered for.
    [Fact]
    public void TheProviderMenusSayWhatEachProviderHasForAKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _store.SetEnvironmentKey(AssistantProviders.OpenAi, "sk-from-env");

        foreach (var menu in new[] { _vm.BuildProviderMenu(), Review.BuildProviderMenu() })
        {
            var items = menu.ToDictionary(i => i.Label, i => i.Shortcut);
            Assert.Equal("Key saved", items["Anthropic"]);
            Assert.Equal("From environment", items["OpenAI"]);
            Assert.Equal("No key", items["Groq"]);
            Assert.Null(items["Ollama"]);
        }
    }

    // The field holds the stored key rather than a sentence about it. It is masked, and the
    // framework refuses the clipboard over a masked field, so it reads as bullets and nothing else.
    [Fact]
    public void TheCardOpensHoldingTheSavedKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");

        _vm.ResetSettings.Execute();

        Assert.Equal("sk-stored", _vm.KeyDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyHint.Value);
    }

    // A key the app only reads is not the app's to hand back or to take away, so the box stays empty
    // and the line that explains where it comes from stays put.
    [Fact]
    public void AKeyFromTheEnvironmentIsDescribedRatherThanFilledIn()
    {
        _store.SetEnvironmentKey(AssistantProviders.Anthropic, "sk-from-env");

        _vm.ResetSettings.Execute();
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);
        Assert.Equal("Using ANTHROPIC_API_KEY from the environment.", _vm.KeyHint.Value);

        // Saving without touching it leaves the stored key alone rather than reading as a deletion.
        _vm.SaveSettings.Execute();
        Assert.IsType<AssistantKeyEdit.Keep>(_store.SavedKey);
    }

    // Emptying the box only means "forget it" where the box was holding the stored key to begin with.
    [Fact]
    public void EmptyingAFilledKeyFieldForgetsTheStoredKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _vm.ResetSettings.Execute();

        _vm.KeyDraft.Value = string.Empty;
        _vm.SaveSettings.Execute();
        var forget = Assert.IsType<AssistantKeyEdit.Forget>(_store.SavedKey);
        Assert.Equal(AssistantProviders.Anthropic.Id, forget.Provider.Id);

        // And once it is gone, an empty box is just an empty box again.
        _vm.ResetSettings.Execute();
        _vm.SaveSettings.Execute();
        Assert.IsType<AssistantKeyEdit.Keep>(_store.SavedKey);
    }

    // Another provider's key is not read on the way past, so switching away empties the box — and
    // switching back must not then read as a deletion.
    [Fact]
    public void SwitchingProviderAndBackKeepsTheStoredKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-stored");
        _vm.ResetSettings.Execute();

        _vm.SetKeyProviderDraft(AssistantProviders.Groq.Id);
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);

        _vm.SetKeyProviderDraft(AssistantProviders.Anthropic.Id);
        Assert.Equal("sk-stored", _vm.KeyDraft.Value);
    }

    // A default and not a whitelist: the menu fills the field in, and the field keeps whatever is
    // typed instead.
    [Fact]
    public void TheModelMenuOffersTheRolesProvidersOwnAndMarksTheOneInTheField()
    {
        Review.SetProvider(AssistantProviders.OpenAi.Id);
        Assert.True(Review.HasModelPresets.Value);

        var items = Review.BuildModelMenu();
        Assert.Equal(AssistantProviders.OpenAi.ModelPresets, items.Select(i => i.Label));
        Assert.DoesNotContain(items, i => i.Checked);

        items.First(i => i.Label == "gpt-5.6-luna").OnSelected();
        Assert.Equal("gpt-5.6-luna", Review.Model.Value);
        Assert.Single(Review.BuildModelMenu().Where(i => i.Checked));

        // A model that is not on the list is kept as typed rather than rejected.
        Review.Model.Value = "gpt-from-next-year";
        _vm.SaveSettings.Execute();
        Assert.Equal("gpt-from-next-year", _store.Saved!.ModelFor(AssistantRole.Review).Model);
        Assert.DoesNotContain(Review.BuildModelMenu(), i => i.Checked);
    }

    // A local endpoint serves whatever the user pulled, so there is nothing honest to offer.
    [Fact]
    public void ALocalProviderHasNoModelListToOffer()
    {
        Chat.SetProvider(AssistantProviders.Ollama.Id);

        Assert.False(Chat.HasModelPresets.Value);
        Assert.Empty(Chat.BuildModelMenu());
    }

    [Fact]
    public void TheProviderMenusMarkTheOnePicked()
    {
        _vm.SetKeyProviderDraft(AssistantProviders.LmStudio.Id);

        var items = _vm.BuildProviderMenu();
        Assert.Equal(AssistantProviders.All.Count, items.Count);
        var checkedItem = Assert.Single(items.Where(i => i.Checked));
        Assert.Equal("LM Studio", checkedItem.Label);

        items.First(i => i.Label == "Groq").OnSelected();
        Assert.Equal(AssistantProviders.Groq.Id, _vm.KeyProviderDraft.Value);

        Walkthrough.SetProvider(AssistantProviders.Together.Id);
        Assert.Equal("Together", Assert.Single(Walkthrough.BuildProviderMenu().Where(i => i.Checked)).Label);
    }

    private static (string, string?) Pair(AssistantModelChoice choice) => (choice.Provider.Id, choice.Model);

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
        _loc.Dispose();
    }
}

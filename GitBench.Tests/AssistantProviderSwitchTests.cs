using GitBench.App;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// Keeping several providers configured: what the card and the header switcher offer, what each
/// provider remembers, and — the one that matters — whose slot a key can reach.
public sealed class AssistantProviderSwitchTests : IDisposable
{
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeAssistantSessionStore _store = new();
    private readonly AssistantViewModel _vm;

    public AssistantProviderSwitchTests()
    {
        _vm = new AssistantViewModel(_store, _loc, new MessageBus());
    }

    // The live bug: the card was pre-filled with the key of whichever provider had last resolved,
    // the picker was moved, and the save wrote those bullets into the new provider's slot. Both
    // files ended up holding the same sk-proj- key.
    [Fact]
    public void SwitchingTheCardsProviderNeverSavesTheOtherProvidersKey()
    {
        _store.SetSavedKey(AssistantProviders.OpenAi, "sk-proj-openai");
        _store.Save(AssistantSettings.For(AssistantProviders.OpenAi.Id), apiKey: null);
        _store.Writes.Clear();

        _vm.OpenSettings.Execute();
        Assert.Equal("sk-proj-openai", _vm.KeyDraft.Value);

        _vm.SetProviderDraft(AssistantProviders.Anthropic.Id);
        _vm.SaveSettings.Execute();

        var write = Assert.Single(_store.Writes);
        Assert.Equal(AssistantProviders.Anthropic.Id, write.ProviderId);
        // Null is "leave whatever is stored alone". Anything else here would be OpenAI's key.
        Assert.Null(write.ApiKey);
        Assert.Null(_store.KeyStateFor(AssistantProviders.Anthropic).SavedKey);
        Assert.Equal("sk-proj-openai", _store.KeyStateFor(AssistantProviders.OpenAi).SavedKey);
    }

    // The same rule the other way round: a key typed for the provider on screen is saved, and it is
    // saved under that provider.
    [Fact]
    public void AKeyTypedForTheProviderOnScreenIsSavedUnderThatProvider()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-ant-existing");

        _vm.OpenSettings.Execute();
        _vm.SetProviderDraft(AssistantProviders.OpenAi.Id);
        _vm.KeyDraft.Value = "sk-proj-typed";
        _vm.SaveSettings.Execute();

        var write = Assert.Single(_store.Writes);
        Assert.Equal(AssistantProviders.OpenAi.Id, write.ProviderId);
        Assert.Equal("sk-proj-typed", write.ApiKey);
        Assert.Equal("sk-ant-existing", _store.KeyStateFor(AssistantProviders.Anthropic).SavedKey);
    }

    [Fact]
    public void AProvidersModelSurvivesASwitchAwayAndBack()
    {
        _vm.OpenSettings.Execute();
        _vm.ModelDraft.Value = "claude-sonnet-5";
        _vm.SaveSettings.Execute();

        _vm.SetProviderDraft(AssistantProviders.OpenAi.Id);
        Assert.Equal(string.Empty, _vm.ModelDraft.Value);
        _vm.ModelDraft.Value = "gpt-5.6-terra";
        _vm.SaveSettings.Execute();

        _vm.SetProviderDraft(AssistantProviders.Anthropic.Id);
        Assert.Equal("claude-sonnet-5", _vm.ModelDraft.Value);

        var settings = _store.Settings.Value;
        Assert.Equal("claude-sonnet-5", settings.ChoiceFor(AssistantProviders.Anthropic.Id).Model);
        Assert.Equal("gpt-5.6-terra", settings.ChoiceFor(AssistantProviders.OpenAi.Id).Model);
    }

    // The endpoint is the same kind of thing: it belongs to the provider it was typed for.
    [Fact]
    public void AProvidersEndpointIsRememberedWithIt()
    {
        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);
        _vm.BaseUrlDraft.Value = "http://localhost:9999/v1";
        _vm.SaveSettings.Execute();

        _vm.SetProviderDraft(AssistantProviders.LmStudio.Id);
        Assert.Equal(string.Empty, _vm.BaseUrlDraft.Value);

        _vm.SetProviderDraft(AssistantProviders.Ollama.Id);
        Assert.Equal("http://localhost:9999/v1", _vm.BaseUrlDraft.Value);
    }

    [Fact]
    public void TheHeaderSwitcherOffersOnlyProvidersThatCanAnswerAndMarksTheActiveOne()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-ant");
        _store.SetEnvironmentKey(AssistantProviders.OpenAi, "sk-from-env");

        var items = _vm.BuildProviderSwitcher();
        var providers = items.Where(i => !i.IsSeparator).Select(i => i.Label).ToArray();

        // A saved key, an inherited one, and the local endpoints that need none.
        Assert.Contains("Anthropic", providers);
        Assert.Contains("OpenAI", providers);
        Assert.Contains("Ollama", providers);
        Assert.DoesNotContain("Groq", providers);

        var marked = Assert.Single(items.Where(i => i.Checked));
        Assert.Equal("Anthropic", marked.Label);
        Assert.Equal("Set up another provider…", items[^1].Label);
    }

    [Fact]
    public void SwitchingFromTheHeaderRepointsTheAssistantWithoutTouchingAnyKey()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-ant");
        _store.SetSavedKey(AssistantProviders.OpenAi, "sk-proj-openai");
        _store.Writes.Clear();

        _vm.BuildProviderSwitcher().First(i => i.Label == "OpenAI").OnSelected();

        var write = Assert.Single(_store.Writes);
        Assert.Equal(AssistantProviders.OpenAi.Id, write.ProviderId);
        Assert.Null(write.ApiKey);
        Assert.Equal("sk-ant", _store.KeyStateFor(AssistantProviders.Anthropic).SavedKey);
        Assert.Equal("sk-proj-openai", _store.KeyStateFor(AssistantProviders.OpenAi).SavedKey);
    }

    // Nothing offers it, but nothing may quietly accept it either: a provider with no key becomes a
    // trip through the card rather than a connection that cannot sign a request.
    [Fact]
    public void PickingAProviderWithNoKeyOpensSetupInsteadOfPointingAtIt()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "sk-ant");
        _store.Writes.Clear();

        _vm.SwitchProvider(AssistantProviders.Groq.Id);

        Assert.Empty(_store.Writes);
        Assert.Equal(AssistantProviders.Anthropic.Id, _store.Settings.Value.ProviderId);
        Assert.True(_vm.ShowSettings.Value);
        Assert.Equal(AssistantProviders.Groq.Id, _vm.ProviderDraft.Value);
        Assert.Equal(string.Empty, _vm.KeyDraft.Value);
    }

    // The tiers resolve through the provider, so a quick action after a swap must run on the new
    // provider's quick model rather than the one the last provider was given.
    [Fact]
    public void AQuickActionAfterASwapRunsOnTheNewProvidersQuickModel()
    {
        var settings = AssistantSettings
            .For(AssistantProviders.Anthropic.Id, "claude-sonnet-5")
            .Select(AssistantProviders.OpenAi.Id);

        var connection = settings.Connect("sk-proj-openai");

        Assert.Equal(AssistantProviders.OpenAi.QuickModel, connection.ModelFor(ModelTier.Quick));
        Assert.Equal(AssistantProviders.OpenAi.ChatModel, connection.ModelFor(ModelTier.Chat));

        // And going back restores what that provider was given.
        var back = settings.Select(AssistantProviders.Anthropic.Id);
        Assert.Equal("claude-sonnet-5", back.Connect("sk-ant").ModelFor(ModelTier.Quick));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
        _loc.Dispose();
    }
}

/// <summary>
/// The same rule against the real store and a secret store that, unlike the app's other fakes, keeps
/// one secret per name — which is the only way a key reaching the wrong provider's slot is visible.
/// </summary>
public sealed class AssistantProviderKeyIsolationTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-assistant-keys-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly NamedSecretStore _secrets = new();

    private AssistantSessionStore? _store;
    private AssistantViewModel? _vm;

    // Nothing is saved for Anthropic and a key is saved for OpenAI. The card, opened on Anthropic,
    // must offer an empty box: bullets look the same whichever key they stand for, so a field filled
    // from anything but this provider's own entry is a key the user cannot see is wrong.
    [Fact]
    public void TheCardOffersNoKeyForAProviderThatHasNoneWhileAnotherProviderHasOne()
    {
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var vm = Start(AssistantSettings.For(AssistantProviders.Anthropic.Id));

        vm.OpenSettings.Execute();

        Assert.Equal(string.Empty, vm.KeyDraft.Value);

        SaveAndSettle(vm);

        Assert.Empty(_secrets.WritesTo(AssistantProviders.Anthropic.SecretName));
        Assert.Null(_secrets.Get(AssistantProviders.Anthropic.SecretName));
        Assert.Equal("sk-proj-openai", _secrets.Get(AssistantProviders.OpenAi.SecretName));
    }

    // The full gesture the user made: the card open on the provider that has a key, the picker moved
    // to one that has none, save. The untouched provider's slot must not be written at all.
    [Fact]
    public void SavingAfterMovingThePickerWritesNothingIntoTheOtherProvidersSlot()
    {
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var vm = Start(AssistantSettings.For(AssistantProviders.OpenAi.Id));

        vm.OpenSettings.Execute();
        Assert.Equal("sk-proj-openai", vm.KeyDraft.Value);

        vm.SetProviderDraft(AssistantProviders.Anthropic.Id);
        SaveAndSettle(vm);

        Assert.Empty(_secrets.WritesTo(AssistantProviders.Anthropic.SecretName));
        Assert.Null(_secrets.Get(AssistantProviders.Anthropic.SecretName));
        Assert.Equal("sk-proj-openai", _secrets.Get(AssistantProviders.OpenAi.SecretName));
    }

    // Clearing is one provider's business too, exactly as clearing a conversation is one repo's.
    [Fact]
    public void ForgettingAKeyForgetsOnlyThatProvidersKey()
    {
        _secrets.Set(AssistantProviders.Anthropic.SecretName, "sk-ant");
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var vm = Start(AssistantSettings.For(AssistantProviders.Anthropic.Id));

        vm.OpenSettings.Execute();
        vm.KeyDraft.Value = string.Empty;
        SaveAndSettle(vm);

        Assert.Null(_secrets.Get(AssistantProviders.Anthropic.SecretName));
        Assert.Equal("sk-proj-openai", _secrets.Get(AssistantProviders.OpenAi.SecretName));
    }

    // The self-hosted providers take a key too now, so the rule has to hold for them: a gateway token
    // typed for one lands in that provider's slot and touches no other.
    [Fact]
    public void AGatewayTokenTypedForASelfHostedProviderReachesOnlyItsOwnSlot()
    {
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var vm = Start(AssistantSettings.For(AssistantProviders.Ollama.Id));

        vm.OpenSettings.Execute();
        Assert.Equal(string.Empty, vm.KeyDraft.Value);

        vm.BaseUrlDraft.Value = "https://gw.internal/v1";
        vm.KeyDraft.Value = "gateway-token";
        SaveAndSettle(vm);

        Assert.Equal("gateway-token", _secrets.Get(AssistantProviders.Ollama.SecretName));
        Assert.Equal("sk-proj-openai", _secrets.Get(AssistantProviders.OpenAi.SecretName));
        Assert.Empty(_secrets.WritesTo(AssistantProviders.LmStudio.SecretName));
        // The only write OpenAI's slot ever saw is the one this test seeded it with.
        Assert.Single(_secrets.WritesTo(AssistantProviders.OpenAi.SecretName));
        // And it is what signs the next turn.
        Assert.Equal("gateway-token", _store!.Settings.Value.Connect(
            _store.Keys.Value.For(AssistantProviders.Ollama).ApiKey).ApiKey);
    }

    private AssistantViewModel Start(AssistantSettings settings)
    {
        var statePath = Path.Combine(_dir.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _store = new AssistantSessionStore(
            registry,
            new GitService(new NullActivityTracker()),
            new AssistantCredentials(_secrets),
            new State<AssistantSettings>(settings),
            _loc,
            _dispatcher,
            _bus,
            new AssistantViewFixture.FakeCommitEditor(),
            new ReviewProgressStore(),
            _ => new FakeAssistantBackend());
        _store.Start();
        _vm = new AssistantViewModel(_store, _loc, _bus);

        // The keys resolve on a worker and land as one keyring, so every seeded key showing up is
        // the whole pass having landed.
        var seeded = AssistantProviders.All.Where(p => _secrets.Get(p.SecretName) is not null).ToArray();
        Pump.WaitFor(
            _dispatcher,
            () => seeded.All(p => _store.Keys.Value.For(p).SavedKey is not null),
            "the saved keys to resolve");
        return _vm;
    }

    // A save resolves again off the UI thread and lands as a new keyring, so waiting for that one to
    // arrive is waiting for the save to have finished with the secret store.
    private void SaveAndSettle(AssistantViewModel vm)
    {
        var before = _store!.Keys.Value;
        vm.SaveSettings.Execute();
        Pump.WaitFor(
            _dispatcher, () => !ReferenceEquals(_store.Keys.Value, before), "the save to reach the secret store");
    }

    public void Dispose()
    {
        _vm?.Dispose();
        _store?.Dispose();
        _loc.Dispose();
        _dir.Dispose();
    }

}

/// <summary>
/// One secret per name, and every write recorded against the name it was made under. The other
/// assistant fakes keep a single secret whatever the name, which is exactly what would hide a key
/// landing in the wrong provider's slot.
/// </summary>
/// <remarks>
/// Every entry is guarded, because the store is read on a worker while a test is still writing to
/// it, and a write can be parked with <see cref="HoldWrites"/> the way a keyring waiting on an
/// unlock prompt would park it — outside the guard, so a read is free to overtake it.
/// </remarks>
internal sealed class NamedSecretStore : ISecretStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly List<string> _writes = new();

    private WriteHold? _hold;

    public IReadOnlyList<string> WritesTo(string name)
    {
        lock (_sync) return _writes.Where(w => string.Equals(w, name, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Parks the next write inside the store until the returned hold is released.</summary>
    public WriteHold HoldWrites()
    {
        var hold = new WriteHold(() => Volatile.Write(ref _hold, null));
        Volatile.Write(ref _hold, hold);
        return hold;
    }

    public string? Get(string name)
    {
        lock (_sync) return _secrets.GetValueOrDefault(name);
    }

    public bool Set(string name, string secret)
    {
        Volatile.Read(ref _hold)?.Enter();
        lock (_sync)
        {
            _writes.Add(name);
            _secrets[name] = secret;
            return true;
        }
    }

    public bool Delete(string name)
    {
        Volatile.Read(ref _hold)?.Enter();
        lock (_sync)
        {
            _writes.Add(name);
            _secrets.Remove(name);
            return true;
        }
    }

    internal sealed class WriteHold : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _released = new(false);
        private readonly Action _forget;

        public WriteHold(Action forget) => _forget = forget;

        internal void Enter()
        {
            _entered.Set();
            _released.Wait(TimeSpan.FromSeconds(10));
        }

        /// <summary>Blocks until a write has actually reached the store and parked there.</summary>
        public void WaitUntilEntered() =>
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(10)), "no write reached the secret store");

        public void Release() => _released.Set();

        public void Dispose()
        {
            _forget();
            _released.Set();
        }
    }
}

/// <summary>
/// When a switch takes effect, as against when it is announced. The store hands the backend a
/// delegate the router calls once per request, so what that delegate answers is literally what signs
/// the next turn — and the secret store, which is milliseconds away at best and an unlock prompt away
/// at worst, must not be between the two.
/// </summary>
public sealed class AssistantProviderSwitchTimingTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-assistant-timing-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly NamedSecretStore _secrets = new();
    private readonly FakeAssistantBackend _backend = new();

    // Every provider that needs a key also reads one from the environment, and a developer machine
    // usually has one exported. What is being asserted here is what the app itself knows, so the
    // process's own variables are put aside for the duration — which is the state CI runs in anyway.
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);

    private AssistantSessionStore? _store;
    private Func<AssistantConnection>? _connection;

    public AssistantProviderSwitchTimingTests()
    {
        foreach (var variable in AssistantProviders.All.Select(p => p.EnvironmentVariable).OfType<string>())
        {
            _environment[variable] = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // The gesture: a user on one provider picks another that is already set up, and types. Nothing
    // is drained between the switch and the read, because nothing is drained between a click and the
    // keystroke after it either — the switch has to stand on what is already known.
    [Fact]
    public void SwitchingToAProviderAlreadySetUpRepointsTheConnectionBeforeTheSecretStoreAnswers()
    {
        _secrets.Set(AssistantProviders.Anthropic.SecretName, "sk-ant");
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var store = Start(AssistantSettings.For(AssistantProviders.Anthropic.Id));
        var keyring = store.Keys.Value;

        store.Save(store.Settings.Value.Select(AssistantProviders.OpenAi.Id), apiKey: null);

        // The keyring is still the one from before the switch, which is this test saying that no
        // answer from the secret store has landed yet — and the connection has moved regardless.
        Assert.Same(keyring, store.Keys.Value);
        var connection = _connection!();
        Assert.Equal(AssistantProviders.OpenAi.Id, connection.Provider.Id);
        Assert.Equal("sk-proj-openai", connection.ApiKey);
        Assert.True(store.IsConfigured.Value);
    }

    // And when the secret store does answer, it confirms rather than changes: the provider left
    // behind never gets a turn back.
    [Fact]
    public void TheResolveThatFollowsASwitchOnlyConfirmsWhatTheSwitchAlreadyDid()
    {
        _secrets.Set(AssistantProviders.Anthropic.SecretName, "sk-ant");
        _secrets.Set(AssistantProviders.OpenAi.SecretName, "sk-proj-openai");
        var store = Start(AssistantSettings.For(AssistantProviders.Anthropic.Id));
        var keyring = store.Keys.Value;

        store.Save(store.Settings.Value.Select(AssistantProviders.OpenAi.Id), apiKey: null);
        Assert.NotEqual("sk-ant", _connection!().ApiKey);

        Pump.WaitFor(_dispatcher, () => !ReferenceEquals(store.Keys.Value, keyring), "the resolve to land");
        Assert.Equal(AssistantProviders.OpenAi.Id, _connection().Provider.Id);
        Assert.Equal("sk-proj-openai", _connection().ApiKey);
        Assert.True(store.IsConfigured.Value);
    }

    // A provider the app has no key for yet is not a connection, so the assistant closes for the
    // duration rather than letting anything through to the provider being left behind. The preset
    // asks for that gate itself: it has no composer to grey out, and it carries a diff.
    [Fact]
    public void SwitchingToAProviderWhoseKeyIsNotKnownYetClosesTheAssistantUntilItLands()
    {
        var store = Start(AssistantSettings.For(AssistantProviders.Ollama.Id), withRepo: true);
        var session = store.Active.Value!;
        Assert.True(store.IsConfigured.Value);

        // Put there behind the app's back, so the switch below is to a provider whose key only the
        // secret store knows about.
        _secrets.Set(AssistantProviders.Anthropic.SecretName, "sk-ant");
        store.Save(store.Settings.Value.Select(AssistantProviders.Anthropic.Id), apiKey: null);

        Assert.False(store.IsConfigured.Value);

        store.RunPreset(AgentCatalog.ExplainSelectionAgent, "explain this diff");
        Assert.Empty(session.Rows);
        Assert.Empty(_backend.Requests);

        Pump.WaitFor(_dispatcher, () => store.IsConfigured.Value, "the key to resolve");
        Assert.Equal("sk-ant", _connection!().ApiKey);
    }

    // Race B: the resolve counter orders the requests, not the reads and writes they make. A second
    // pass reading the store while the first is still writing to it used to win the counter and
    // publish a keyring without the key just typed — leaving the provider looking unconfigured.
    [Fact]
    public void AKeySavedAndSwitchedAwayFromInTheSameBreathIsStillTheKeyThatProviderHas()
    {
        var store = Start(AssistantSettings.For(AssistantProviders.Anthropic.Id));

        using var writing = _secrets.HoldWrites();
        store.Save(store.Settings.Value, "sk-ant-typed");
        writing.WaitUntilEntered();

        store.Save(store.Settings.Value.Select(AssistantProviders.Ollama.Id), apiKey: null);
        // Long enough for a second pass to have read the store, had it been free to.
        Thread.Sleep(50);
        writing.Release();

        Pump.WaitFor(
            _dispatcher,
            () => store.Keys.Value.For(AssistantProviders.Anthropic).SavedKey == "sk-ant-typed",
            "the saved key to survive the switch");
        Assert.Equal("sk-ant-typed", _secrets.Get(AssistantProviders.Anthropic.SecretName));
    }

    private AssistantSessionStore Start(AssistantSettings settings, bool withRepo = false)
    {
        var statePath = Path.Combine(_dir.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        if (withRepo)
        {
            var root = Path.Combine(_dir.Path, "repo");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.Equal(OpenRepoOutcome.Opened, registry.Open(root));
        }

        _store = new AssistantSessionStore(
            registry,
            new GitService(new NullActivityTracker()),
            new AssistantCredentials(_secrets),
            new State<AssistantSettings>(settings),
            _loc,
            _dispatcher,
            _bus,
            new AssistantViewFixture.FakeCommitEditor(),
            new ReviewProgressStore(),
            connection =>
            {
                _connection = connection;
                return _backend;
            });
        _store.Start();

        // The first pass reads every provider at once and lands as one keyring, so a keyring that is
        // no longer the empty one is the whole pass having arrived — including for a store that was
        // seeded with nothing.
        Pump.WaitFor(
            _dispatcher,
            () => !ReferenceEquals(_store.Keys.Value, AssistantKeyring.Empty)
                && (!withRepo || _store.Active.Value is not null),
            "the first pass over the secret store");
        return _store;
    }

    public void Dispose()
    {
        foreach (var (variable, value) in _environment)
            Environment.SetEnvironmentVariable(variable, value);
        _store?.Dispose();
        _loc.Dispose();
        _dir.Dispose();
    }
}

/// Nothing is being tracked here: the git service these tests hand out is never asked to read.
internal sealed class NullActivityTracker : IRepoActivityTracker
{
    private sealed class Scope : IDisposable { public void Dispose() { } }

    public IDisposable Begin(string repoPath) => new Scope();
    public bool IsActive(string repoPath) => false;
}

/// What a provider switch does to a conversation already under way.
public sealed class AssistantProviderSwitchConversationTests : IDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));

    // A tool-call id belongs to the provider that issued it — Anthropic validates the id it is given
    // and strict OpenAI-compatible endpoints (Mistral's, and anything routed to it) refuse an id not
    // in their own shape. So the exchange the model is sent starts again at the switch, and the
    // proof is that no id from before it is ever replayed.
    [Fact]
    public void NoToolCallIdFromTheOldProviderIsSentAgainAfterASwitch()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("toolu_01ABCDEF", "alpha", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("the branch is main"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("still here"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        using var session = Session(backend);
        Ask(session, "which branch am I on?");

        // The thread now carries an id the old provider issued, and replays it every turn.
        Assert.Contains(ToolUses(backend.Requests[1]), use => use.Id == "toolu_01ABCDEF");

        session.RestartForProviderChange("Now using OpenAI.");
        Ask(session, "and now?");

        var sent = backend.Requests[^1];
        Assert.Empty(ToolUses(sent));
        Assert.Empty(sent.Messages.OfType<AssistantMessage.ToolResults>());
        var user = Assert.IsType<AssistantMessage.User>(sent.Messages[0]);
        Assert.Equal("and now?", user.Text);
    }

    // What was said is still what was said: the switch marks the transcript rather than emptying it.
    [Fact]
    public void TheTranscriptKeepsWhatWasSaidAndSaysWhereTheSwitchHappened()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("on main"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        using var session = Session(backend);
        Ask(session, "which branch am I on?");
        var before = session.Rows.Count;

        session.RestartForProviderChange("Now using OpenAI.");

        Assert.Equal(before + 1, session.Rows.Count);
        var notice = session.Rows[^1];
        Assert.Equal(AssistantRowKind.Notice, notice.Kind);
        Assert.Equal("Now using OpenAI.", notice.Text.Value);
    }

    // Nothing was going to be replayed, so there is nothing to say about it not being.
    [Fact]
    public void AnUntouchedTranscriptGetsNoNotice()
    {
        using var session = Session(new FakeAssistantBackend());

        session.RestartForProviderChange("Now using OpenAI.");

        Assert.Empty(session.Rows);
    }

    // A clear undone after a switch would put the old provider's tool-call ids back into the thread,
    // so the offer goes with the messages.
    [Fact]
    public void UndoingAClearFromBeforeTheSwitchDoesNotRestoreTheOldThread()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("toolu_01ABCDEF", "alpha", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("on main"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("still here"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        using var session = Session(backend);
        Ask(session, "which branch am I on?");

        session.Clear();
        session.RestartForProviderChange("Now using OpenAI.");
        session.UndoClear();

        Ask(session, "and now?");
        Assert.Empty(ToolUses(backend.Requests[^1]));
    }

    // The rule belongs to the app, not to one class in it: pointing the store at another provider is
    // what has to leave the next request free of the last provider's ids.
    [Fact]
    public void PointingTheStoreAtAnotherProviderRestartsTheRepositorysThread()
    {
        using var dir = new TempDir("gitbench-assistant-switch-");
        var root = Path.Combine(dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var statePath = Path.Combine(dir.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, registry.Open(root));

        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("toolu_01ABCDEF", "get_status", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("on main"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("still here"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        // Two local providers, so the switch turns on nothing but the provider itself.
        var settings = new State<AssistantSettings>(AssistantSettings.For(AssistantProviders.Ollama.Id));
        using var store = new AssistantSessionStore(
            registry,
            new GitService(new NullActivityTracker()),
            new AssistantCredentials(new NoopSecretStore()),
            settings,
            _loc,
            _dispatcher,
            new MessageBus(),
            new AssistantViewFixture.FakeCommitEditor(),
            new ReviewProgressStore(),
            _ => backend);
        store.Start();
        Pump.WaitFor(_dispatcher, () => store.Active.Value is not null, "the repository's session");

        var session = store.Active.Value!;
        Ask(session, "which branch am I on?");
        Assert.Contains(ToolUses(backend.Requests[1]), use => use.Id == "toolu_01ABCDEF");

        store.Save(settings.Value.Select(AssistantProviders.LmStudio.Id), apiKey: null);
        Ask(session, "and now?");

        Assert.Empty(ToolUses(backend.Requests[^1]));
        Assert.Contains(session.Rows, r =>
            r.Kind == AssistantRowKind.Notice && r.Text.Value.Contains("LM Studio", StringComparison.Ordinal));
    }

    private AssistantSession Session(FakeAssistantBackend backend)
    {
        var agent = new AgentDefinition("test", "You are a test agent.", ["alpha"], ModelTier.Chat);
        var loop = new AssistantAgentLoop(
            backend, agent, AssistantToolset.Create([new StubTool("alpha")], ["alpha"]));
        var repo = new Repo(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "repo"), "repo");
        return new AssistantSession(repo, loop, _loc, _dispatcher);
    }

    private void Ask(AssistantSession session, string message)
    {
        session.Send(message);
        Pump.WaitFor(_dispatcher, () => !session.IsBusy.Value, "the turn to finish");
    }

    private static IReadOnlyList<AssistantContent.ToolUse> ToolUses(AssistantTurn turn) =>
        turn.Messages
            .OfType<AssistantMessage.Assistant>()
            .SelectMany(m => m.Content)
            .OfType<AssistantContent.ToolUse>()
            .ToArray();

    public void Dispose() => _loc.Dispose();
}

/// The overrides are kept per provider now; a file written before they were must not lose the one
/// it has.
public sealed class AssistantProviderPreferencesTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-assistant-prefs-");

    [Fact]
    public void TheFlatFieldsMigrateOntoTheProviderThatWasSelected()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, """
        {
          "assistantProviderId": "openai",
          "assistantModel": "gpt-5.6-terra",
          "assistantBaseUrl": null
        }
        """);

        var loaded = PreferencesStore.Load(path);

        var choice = Assert.Single(loaded.AssistantProviderPreferences);
        Assert.Equal("openai", choice.ProviderId);
        Assert.Equal("gpt-5.6-terra", choice.Model);

        var settings = AssistantSettings.From(
            loaded.AssistantProviderId,
            loaded.AssistantProviderPreferences.Select(c => (c.ProviderId, c.Model, c.BaseUrl)));
        Assert.Equal("gpt-5.6-terra", settings.Model);
    }

    [Fact]
    public void EveryProvidersChoiceSurvivesASaveAndLoad()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        var service = new PreferencesService(Preferences.Default, path);
        service.SetAssistantProvider(
            "openai",
            [
                new AssistantProviderPreference("anthropic", "claude-sonnet-5", null),
                new AssistantProviderPreference("openai", "gpt-5.6-terra", null),
            ]);
        service.Dispose();

        var loaded = PreferencesStore.Load(path);

        Assert.Equal("openai", loaded.AssistantProviderId);
        Assert.Equal(2, loaded.AssistantProviderPreferences.Count);

        var settings = AssistantSettings.From(
            loaded.AssistantProviderId,
            loaded.AssistantProviderPreferences.Select(c => (c.ProviderId, c.Model, c.BaseUrl)));
        Assert.Equal("gpt-5.6-terra", settings.Model);
        Assert.Equal("claude-sonnet-5", settings.ChoiceFor("anthropic").Model);
    }

    // An id this build has never heard of stands for itself or for nothing — never for the provider
    // the resolver happens to fall back to.
    [Fact]
    public void AnUnknownProviderIdIsDroppedRatherThanAppliedToTheDefaultProvider()
    {
        var settings = AssistantSettings.From(
            "anthropic",
            [("provider-from-next-year", "some-model", null)]);

        Assert.Null(settings.Model);
        Assert.Null(settings.ChoiceFor("anthropic").Model);
    }

    public void Dispose() => _dir.Dispose();
}

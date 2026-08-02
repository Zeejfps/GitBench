using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitBench.App;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The commit bar's "Generate commit message": the agent the catalog picks up, the pipeline it shares
// with the chat, and where its output and its failures land. The commit box asserted against is the
// real LocalChangesViewModel, because the point of the action is that the text arrives in the box the
// person is looking at.
public sealed class AssistantCommitMessageTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git = new(new NullActivityTracker());
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly LocalChangesViewModel _commitBox;

    private AssistantSessionStore? _store;
    private AssistantViewModel? _vm;

    public AssistantCommitMessageTests()
    {
        _root = NewDir();
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
        Git("add", "a.txt");
        Git("-c", "commit.gpgsign=false", "commit", "-m", "seed the tree");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo\n");
        Git("add", "a.txt");

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_root));
        _registry.SetActive(_registry.Repos.Single().Id);

        _commitBox = new LocalChangesViewModel(
            _registry,
            _git, _git, _git, _git, _git,
            _dispatcher,
            new FrameTicker(),
            _bus,
            new LocalChangesSelectionStore(),
            new NoopShell(),
            new NoopClipboard(),
            new PreferencesService(Preferences.Default, Path.Combine(_root, "prefs.json")),
            new IdleSnapshotStore(),
            _loc);
    }

    // Adding an agent is adding a file, so the assertion that matters is that the shipped .md is
    // picked up with the tier and the tool list it declares — not that some C# registration exists.
    [Fact]
    public void CommitMessageAgent_LoadsFromTheEmbeddedPromptOnTheQuickTier()
    {
        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.CommitMessageAgent);

        Assert.Equal(ModelTier.Quick, agent.Tier);
        Assert.False(AssistantConnection.Default.Capabilities(agent.Tier).MidConversationSystem);
        Assert.NotEmpty(agent.SystemPrompt);
        Assert.DoesNotContain("---", agent.SystemPrompt);
        Assert.Equal(
            new[] { "get_diff", "get_local_changes", "set_commit_message" },
            agent.AllowedTools.OrderBy(t => t, StringComparer.Ordinal));
    }

    // The allowed list is only half of it: the toolset built from it is what the model is actually
    // offered. It carries the one write the button asked for and nothing else — this agent may fill
    // the commit box, and may not stage or commit anything.
    [Fact]
    public void CommitMessageToolset_CarriesTheReadsAndTheOneWriteThatFillsTheBox()
    {
        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.CommitMessageAgent);
        var toolset = AssistantToolset.ForRepo(
            _git, ActiveRepo(), agent, new ReviewProgressStore(), WriteSurface());

        Assert.Equal(
            new[] { "get_diff", "get_local_changes", "set_commit_message" },
            toolset.Tools.Select(t => t.Name));
        Assert.Equal(
            new[] { "set_commit_message" },
            toolset.Tools.Where(t => t.IsWrite).Select(t => t.Name));
        Assert.Null(toolset.Find("commit"));
        Assert.Null(toolset.Find("stage_files"));
    }

    // The two fields arrive as two arguments, so neither has to be recovered from the shape of a
    // reply: they land in the two fields the person would have typed them into.
    [Fact]
    public void TheToolCall_FillsBothFieldsThroughTheCommitBox()
    {
        var vm = Start(Writing(
            "Add a second line to the seed file",
            "The fixture needed a change to\ndiff against."));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Add a second line to the seed file", _commitBox.Title.Value);
        Assert.Equal("The fixture needed a change to\ndiff against.", _commitBox.Description.Value);
        Assert.Null(vm.CommitMessageError.Value);
    }

    // The bug this call replaced: the reply used to be the message, so a preamble, a fence or a
    // sentence about what it was doing became the subject line. Prose around the call is now prose.
    [Fact]
    public void WhateverTheModelSaysAroundTheCall_NeverReachesTheCommitBox()
    {
        var vm = Start(Writing(
            "Add a second line to the seed file",
            saying: "Here's the commit message for your staged changes:\n\n```\n"));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Add a second line to the seed file", _commitBox.Title.Value);
        Assert.Null(vm.CommitMessageError.Value);
    }

    // And a turn that only talks has written no message at all. Saying so beats typing the sentence
    // into the subject line, which is what "nothing is staged" used to look like.
    [Fact]
    public void AReplyWithNoToolCall_IsReportedRatherThanTypedIntoTheBox()
    {
        _commitBox.SetTitle("Typed by hand");
        var vm = Start(Answering("There is nothing staged to commit."));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Typed by hand", _commitBox.Title.Value);
        Assert.NotNull(vm.CommitMessageError.Value);
        Assert.Contains("nothing staged", vm.CommitMessageError.Value!, StringComparison.Ordinal);
    }

    // Omitting the body is a decision the prompt licenses. The call says the message has no body, so
    // the box ends up holding the message and nothing else — the undo below is what covers a
    // description the person wrote themselves.
    [Fact]
    public void ACallWithoutADescription_LeavesTheBoxHoldingTheMessageAlone()
    {
        _commitBox.SetDescription("Written by hand.");
        var vm = Start(Writing("Add a second line to the seed file"));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Add a second line to the seed file", _commitBox.Title.Value);
        Assert.Equal(string.Empty, _commitBox.Description.Value);
    }

    // An endpoint whose tool calling was never demonstrated is the one case where the reply is still
    // the message: the loop reports that it never called a tool, and the old parse runs — labels and
    // quotes stripped, the body after the blank line kept.
    [Fact]
    public void AnEndpointThatCannotCallTools_StillGetsItsReplyRead()
    {
        var vm = Start(
            Answering("Title: \"Add a second line\"\n\nThat is what the diff does.\n"),
            settings: AssistantSettings.For(AssistantProviders.Ollama.Id));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Add a second line", _commitBox.Title.Value);
        Assert.Equal("That is what the diff does.", _commitBox.Description.Value);
        Assert.Null(vm.CommitMessageError.Value);
    }

    // Two fields overwritten at once is more than the person can retype from memory, so the write
    // hands back what it replaced rather than standing a confirmation in front of every generation.
    [Fact]
    public void TheWrite_OffersAnUndoThatRestoresBothFields()
    {
        _commitBox.SetTitle("Typed by hand");
        _commitBox.SetDescription("And a description too.");

        ToastIntent? toast = null;
        using var subscription = _bus.SubscribeScoped<ShowToastMessage>(m => toast = m.Intent);

        var vm = Start(Writing("Add a second line", "Because the fixture needed one."));
        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Equal("Add a second line", _commitBox.Title.Value);
        Assert.Equal("Because the fixture needed one.", _commitBox.Description.Value);

        Assert.NotNull(toast);
        Assert.NotNull(toast!.Action);
        toast.Action!.Invoke();

        Assert.Equal("Typed by hand", _commitBox.Title.Value);
        Assert.Equal("And a description too.", _commitBox.Description.Value);
    }

    // Quick is Haiku, which rejects mid-conversation {"role":"system"} entries, so the live repo
    // block has to ride in a user turn. The branch lives in the request writer; this asserts the
    // quick action actually goes down it rather than around it.
    [Fact]
    public void QuickTier_PutsTheRepoBlockInAUserTurnRatherThanAMidConversationSystemMessage()
    {
        var backend = Answering("Add a second line");
        var vm = Start(backend);

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        var turn = Assert.Single(backend.Requests);
        Assert.Equal(ModelTier.Quick, turn.Tier);
        var context = Assert.Single(turn.Messages.OfType<AssistantMessage.RepoContext>());
        Assert.Contains("Path: " + _root, context.Text, StringComparison.Ordinal);

        using var wire = JsonDocument.Parse(
            Encoding.UTF8.GetString(AnthropicRequestWriter.Write(
                turn, Array.Empty<IAssistantTool>(), AssistantConnection.Default)));
        var messages = wire.RootElement.GetProperty("messages");
        foreach (var message in messages.EnumerateArray())
            Assert.NotEqual("system", message.GetProperty("role").GetString());

        var block = messages[messages.GetArrayLength() - 1];
        Assert.Equal("user", block.GetProperty("role").GetString());
        Assert.Equal(context.Text, block.GetProperty("content")[0].GetProperty("text").GetString());
    }

    // Generated messages follow the UI language like every other agent, and the instruction rides in
    // the per-turn context block rather than the cached system prefix.
    [Fact]
    public void QuickTier_AsksForTheUiReplyLanguage()
    {
        var backend = Answering("Add a second line");
        using var japanese = new LocalizationService(new State<Locale>(Locale.Ja));
        var vm = Start(backend, japanese);

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        var context = Assert.Single(Assert.Single(backend.Requests).Messages.OfType<AssistantMessage.RepoContext>());
        Assert.Contains("Reply in Japanese.", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedGeneration_SurfacesRatherThanDoingNothing()
    {
        var vm = Start(new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.Error("overloaded", "overloaded_error"),
        }));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.NotNull(vm.CommitMessageError.Value);
        Assert.Contains("overloaded", vm.CommitMessageError.Value!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _commitBox.Title.Value);
        // Reported, not stuck: the action can be tried again.
        Assert.True(vm.GenerateCommitMessage.CanExecute.Value);
    }

    // A turn that ends without saying anything is the quiet failure this has to catch: the box would
    // otherwise be left exactly as it was with no explanation.
    [Fact]
    public void AnAnswerlessTurn_IsReportedInsteadOfClearingTheBox()
    {
        var vm = Start(new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        }));

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.NotNull(vm.CommitMessageError.Value);
        Assert.Equal(string.Empty, _commitBox.Title.Value);
    }

    [Fact]
    public void ASecondRequestWhileOneIsRunning_IsDroppedRatherThanQueued()
    {
        var backend = Answering("Add a second line");
        var vm = Start(backend);

        vm.GenerateCommitMessage.Execute();
        Assert.True(vm.IsGeneratingMessage.Value);
        Assert.False(vm.GenerateCommitMessage.CanExecute.Value);

        vm.GenerateCommitMessage.Execute();
        Settle(vm);

        Assert.Single(backend.Requests);
    }

    [Fact]
    public void CommitMenu_OffersGenerateReviewAndChat()
    {
        var vm = Start(Answering("Add a second line"));

        var items = vm.BuildCommitMenu();

        Assert.Equal(3, items.Count);
        Assert.Equal("Generate commit message", items[0].Label);
        Assert.True(items[0].Enabled);
        Assert.Equal("Review changes", items[1].Label);
        Assert.True(items[1].Enabled);
        Assert.Equal("Chat…", items[2].Label);

        // "Chat…" opens the same overlay Ctrl/Cmd+K toggles, rather than toggling it shut.
        Assert.False(vm.IsOpen.Value);
        items[2].OnSelected();
        Assert.True(vm.IsOpen.Value);
        items[2].OnSelected();
        Assert.True(vm.IsOpen.Value);
    }

    [Fact]
    public void CommitMenu_SaysItIsWorkingAndWillNotStartASecondRun()
    {
        var vm = Start(Answering("Add a second line"));

        vm.BuildCommitMenu()[0].OnSelected();

        var running = vm.BuildCommitMenu()[0];
        Assert.Equal("Generating commit message…", running.Label);
        Assert.False(running.Enabled);

        Settle(vm);
        Assert.Equal("Generate commit message", vm.BuildCommitMenu()[0].Label);
    }

    private static FakeAssistantBackend Answering(string text) =>
        new(new BackendEvent[]
        {
            new BackendEvent.TextDelta(text),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });

    /// <summary>A turn that writes the message the way the agent is told to: a
    /// <c>set_commit_message</c> call, optionally after whatever prose the model felt like writing.
    /// The turn that follows the tool result falls off the end of the script and completes.</summary>
    private static FakeAssistantBackend Writing(string title, string? description = null, string? saying = null)
    {
        var arguments = AssistantTestJson.Element(ToolJson.Write(writer =>
        {
            writer.WriteString("title", title);
            if (description is not null) writer.WriteString("description", description);
        }));

        var turn = new List<BackendEvent>();
        if (saying is not null) turn.Add(new BackendEvent.TextDelta(saying));
        turn.Add(new BackendEvent.ToolUse("call_1", SetCommitMessageTool.ToolName, arguments));
        turn.Add(new BackendEvent.TurnComplete(StopReason.ToolUse));

        return new FakeAssistantBackend(turn);
    }

    private AssistantWriteSurface WriteSurface() =>
        new(_dispatcher, _bus, _registry, _commitBox, new IdleRemoteOperations());

    private AssistantViewModel Start(
        FakeAssistantBackend backend,
        LocalizationService? loc = null,
        AssistantSettings? settings = null)
    {
        var localization = loc ?? _loc;
        _store = new AssistantSessionStore(
            _registry,
            _git,
            new AssistantCredentials(new FakeSecretStore("sk-test")),
            new State<AssistantSettings>(settings ?? AssistantSettings.Default),
            localization,
            _dispatcher,
            _bus,
            _commitBox,
            new ReviewProgressStore(),
            new IdleRemoteOperations(),
            _ => backend);
        _store.Start();

        _vm = new AssistantViewModel(_store, localization, _bus);
        // The key resolves on a worker; settle it before anything asks whether the action is offered.
        Pump.WaitFor(_dispatcher, () => _store.IsConfigured.Value, "the API key to resolve");
        return _vm;
    }

    private void Settle(AssistantViewModel vm) =>
        Pump.WaitFor(_dispatcher, () => !vm.IsGeneratingMessage.Value, "the message generation to finish");

    private Repo ActiveRepo() => _registry.Repos.Single();

    public void Dispose()
    {
        _vm?.Dispose();
        _store?.Dispose();
        _commitBox.Dispose();
        _loc.Dispose();
        try { TempDir.ForceDelete(new DirectoryInfo(_root)); } catch { /* best effort */ }
    }

    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitbench-commit-message-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private sealed class NoopShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class NoopClipboard : IClipboard
    {
        public void SetText(string text) { }
        public string? GetText() => null;
    }

    private sealed class IdleSnapshotStore : IRepoSnapshotStore
    {
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges { get; } = new State<Fetched<LocalChangesData>?>(null);
    }

    // Stands in for the OS store, so whatever key the machine running the tests has in its
    // environment cannot change the outcome.
    private sealed class FakeSecretStore : ISecretStore
    {
        private string? _secret;

        public FakeSecretStore(string? secret) => _secret = secret;

        public string? Get(string name) => _secret;

        public bool Set(string name, string secret)
        {
            _secret = secret;
            return true;
        }

        public bool Delete(string name)
        {
            _secret = null;
            return true;
        }
    }
}

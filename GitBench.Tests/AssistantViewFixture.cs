using System.Diagnostics;
using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The assistant's surfaces mounted headlessly over a stand-in workspace, against a scripted backend
/// and a fake secret store: the toolbar button, the overlay, and the pointer spy that reports what
/// the panel let through to the app beneath it.
/// </summary>
internal sealed class AssistantViewFixture : IDisposable
{
    public const int WindowWidth = 900;
    public const int WindowHeight = 700;

    private const float FrameSeconds = 1f / 60f;

    private static readonly InputModifiers Primary =
        OperatingSystem.IsMacOS() ? InputModifiers.Super : InputModifiers.Control;

    private readonly TempDir _dir;
    private readonly AssistantSessionStore _store;
    private readonly RepoRegistry _registry;

    public QueuedDispatcher Dispatcher { get; } = new();
    public MessageBus Bus { get; } = new();
    public GuiTestHarness Harness { get; }
    public AssistantViewModel Vm { get; private set; } = null!;

    /// <summary>Stands in for the commit bar, so a write tool has something to type into.</summary>
    public FakeCommitEditor CommitBox { get; } = new();

    /// <summary>What the transcript's copy affordances write to.</summary>
    public FakeClipboard Clipboard { get; } = new();

    /// <summary>The active catalog, for tests that click a control by its localized label.</summary>
    public ILocalizationService Localization { get; }

    /// <summary>Counts the pointer events the workspace behind the overlay receives.</summary>
    public PointerSpy Underneath { get; } = new();

    public string RepoPath { get; private set; } = string.Empty;

    /// <summary>Where the panel sits, so a test can drive a drag and read back what it did.</summary>
    public AssistantPanelPlacement Placement { get; }

    public PreferencesService Preferences { get; }

    public AssistantViewFixture(
        FakeAssistantBackend backend,
        bool openRepo = true,
        Action<RecordingCanvas>? configureCanvas = null,
        Locale locale = Locale.En,
        Preferences? preferences = null)
    {
        _dir = new TempDir("gitbench-assistant-view-");
        Preferences = new PreferencesService(
            preferences ?? new Preferences(), Path.Combine(_dir.Path, "prefs.json"));
        Placement = new AssistantPanelPlacement(Preferences);
        _registry = new RepoRegistry(
            RepoStateStore.Load(Path.Combine(_dir.Path, "state.json")),
            Path.Combine(_dir.Path, "state.json"));

        if (openRepo)
            RepoPath = OpenRepo("repo");

        var localization = new LocalizationService(new State<Locale>(locale));
        Localization = localization;
        var store = new AssistantSessionStore(
            _registry,
            new GitService(new NullActivityTracker()),
            new UnparsedFiles(),
            new AssistantCredentials(new FakeSecretStore("sk-test")),
            new State<AssistantSettings>(AssistantSettings.Default),
            localization,
            Dispatcher,
            Bus,
            CommitBox,
            new ReviewProgressStore(),
            new IdleRemoteOperations(),
            _ => backend);
        _store = store;

        AppKeybindController keybind = null!;
        Harness = GuiTestHarness.Create(
            ctx =>
            {
                var root = new Stack
                {
                    // The button sits top-leading, clear of the top-trailing overlay, so a click
                    // on it can never land on the open panel instead. Its container chain mirrors
                    // the real actions toolbar — fixed-height bar, horizontal scroller, centered
                    // row — because that chain is what decides the size the mark is drawn at.
                    Children =
                    [
                        // The workspace: a full-window interactive surface under the overlay,
                        // ordered ahead of it exactly as AppContentWidget is.
                        new Box { Width = WindowWidth, Height = WindowHeight }
                            .WithController(ctx.Require<InputSystem>(), () => Underneath),
                        new AssistantOverlay(),
                        new Column
                        {
                            MainAxis = MainAxisAlignment.Start,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Box
                                {
                                    // Kept to the leading half so the toolbar's own scroll view
                                    // does not lie under the top-trailing panel and answer for
                                    // it in the hit test.
                                    Width = 400f,
                                    Height = 44f,
                                    Children =
                                    [
                                        new Padding
                                        {
                                            Amount = new PaddingStyle { Left = 8, Right = 8 },
                                            Children =
                                            [
                                                new HorizontalScrollArea
                                                {
                                                    Child = new Row
                                                    {
                                                        Gap = 2f,
                                                        CrossAxis = CrossAxisAlignment.Center,
                                                        Children = [new AssistantToolbarButton()],
                                                    },
                                                },
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                }.WithController(ctx.Require<InputSystem>(), () => keybind);
                return root.BuildView(ctx);
            },
            width: WindowWidth,
            height: WindowHeight,
            configure: ctx =>
            {
                configureCanvas?.Invoke((RecordingCanvas)ctx.Canvas);
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(localization);
                ctx.AddService<IClipboard>(Clipboard);
                ctx.AddService<IUiDispatcher>(Dispatcher);
                ctx.AddService<IRepoRegistry>(_registry);
                ctx.AddService<IAssistantSessionStore>(store);

                Vm = new AssistantViewModel(store, localization, Bus);
                ctx.AddService(Vm);
                ctx.AddService(Placement);

                keybind = new AppKeybindController(
                    _registry,
                    new RepoHoverState(),
                    new RepoBarCollapseState(Preferences),
                    ctx.Require<ILocalizationService>(),
                    Bus,
                    Vm,
                    new State<MainViewMode>(MainViewMode.LocalChanges),
                    new NoFileBrowsers());
            });

        store.Start();
        // The key resolves on a worker; settle it before anything asks whether setup is needed.
        Pump.WaitFor(Dispatcher, () => store.IsConfigured.Value, "the API key to resolve");

        // Keys reach controllers on the pointer's path, so park the cursor over the tree first.
        Harness.MoveTo(450f, 350f);
    }

    /// <summary>Initialises another repository and adds it to the registry. Used where a behaviour
    /// has to be shown to be one repository's alone.</summary>
    public string OpenRepo(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--initial-branch=main");
        RunGit(path, "config", "user.email", "test@test");
        RunGit(path, "config", "user.name", "test");
        _registry.Open(path);
        return path;
    }

    public IRepoRegistry Registry => _registry;

    public void Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None)
    {
        Harness.PressKey(key, modifiers);
        Harness.Layout();
    }

    public void PressPrimary(KeyboardKey key) => Press(key, Primary);

    // The pane reports its scroll position from layout and only then raises the event the
    // stick-to-bottom rule acts on, so the follow lands on the next pass. Live that is one frame;
    // here it has to be asked for. A frame is the tick as well as the layout: a streamed reply
    // parses its markdown on the tick, so a pass without one shows the transcript one delta behind.
    public void Frames(int count = 2)
    {
        for (var i = 0; i < count; i++) Harness.Tick(FrameSeconds);
    }

    /// <summary>The transcript's scroll pane — the composer's field has its own, so this takes
    /// the one inside the overlay's scroll area rather than the first in the tree.</summary>
    public VerticalScrollPane Pane()
    {
        Frames();
        return Harness.Root.SelfAndDescendants().OfType<VerticalScrollPane>().First();
    }

    public GrowingDescriptionField Field()
    {
        Harness.Layout();
        return (GrowingDescriptionField)Harness.Root.FindById(AssistantComposer.InputId)!;
    }

    /// <summary>Focuses the composer the way a click does, so typed keys reach it.</summary>
    public void FocusComposer()
    {
        Field().BeginEditing();
        Harness.Layout();
    }

    // A real wheel gesture over the transcript, which is what releases the stick-to-bottom pin;
    // scrolling the pane directly would be a programmatic move and deliberately does not.
    private void Wheel(float deltaY, int notches)
    {
        var pane = Pane();
        var center = pane.Position.Center;
        Harness.MoveTo(center.X, center.Y);
        for (var i = 0; i < notches; i++) Harness.Scroll(0f, deltaY);
        Frames();
    }

    public void ScrollTranscriptUp(int notches = 6) => Wheel(1f, notches);

    public void ScrollTranscriptToBottom(int notches = 40) => Wheel(-1f, notches);

    public void ClickToolbarButton()
    {
        Harness.ClickOn(AssistantToolbarButton.ButtonId);
        Harness.Layout();
    }

    /// <summary>Sends a message and pumps until the turn has finished.</summary>
    public void Ask(string message)
    {
        Vm.SetDraft(message);
        Vm.Send.Execute();
        Pump.WaitFor(Dispatcher, () => !Vm.IsBusy.Value, "the assistant turn to finish");
        Frames();
    }

    /// <summary>Sends a message for a turn that is expected to stop and wait rather than finish.</summary>
    public void AskWithoutWaiting(string message)
    {
        Vm.SetDraft(message);
        Vm.Send.Execute();
    }

    /// <summary>Pumps until a write tool's question is on screen.</summary>
    public void WaitForApproval()
    {
        Pump.WaitFor(
            Dispatcher,
            () => Vm.Session.Value?.Rows.Any(r => r.Kind == AssistantRowKind.Approval) ?? false,
            "the approval card to appear");
        Frames();
    }

    /// <summary>Parks the cursor and lets hover settle. A move is dispatched along the path
    /// hover had before it, so the first one after a jump still belongs to the old target.</summary>
    public void HoverAt(float x, float y)
    {
        Harness.MoveTo(x, y);
        Harness.MoveTo(x, y);
        Harness.Layout();
    }

    /// <summary>A press, a drag and a release at one point — the gesture that leaked.</summary>
    public void DragAt(float x, float y)
    {
        Harness.MoveTo(x, y);
        Harness.Press();
        Harness.MoveTo(x + 20f, y + 20f);
        Harness.Release();
        Harness.Layout();
    }

    /// <summary>A full press-drag-release from one point, in window coordinates — where Y runs up the
    /// window, so a downward gesture is a negative <paramref name="dy"/>.</summary>
    public void DragFrom(PointF start, float dx, float dy)
    {
        HoverAt(start.X, start.Y);
        Harness.Press();
        Harness.MoveTo(start.X + dx, start.Y + dy);
        Harness.Release();
        Frames();
    }

    public void WheelAt(float x, float y)
    {
        Harness.MoveTo(x, y);
        Harness.Scroll(0f, -1f);
        Harness.Layout();
    }

    public static bool HasText(RecordingCanvas canvas, string text)
    {
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text.Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Dispose()
    {
        Harness.Dispose();
        Vm.Dispose();
        _store.Dispose();
        Preferences.Dispose();
        _dir.Dispose();
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }

        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    // Records what reaches the surface below the overlay. Consumes nothing, so anything it sees is
    // something the overlay let through.
    internal sealed class PointerSpy : KeyboardMouseController
    {
        public int Presses { get; private set; }
        public int Moves { get; private set; }
        public int Wheels { get; private set; }

        public void Reset()
        {
            Presses = 0;
            Moves = 0;
            Wheels = 0;
        }

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase == EventPhase.Bubbling && e.State == InputState.Pressed) Presses++;
        }

        public override void OnMouseMoved(ref MouseMoveEvent e)
        {
            if (e.Phase == EventPhase.Bubbling) Moves++;
        }

        public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
        {
            if (e.Phase == EventPhase.Bubbling) Wheels++;
        }
    }

    internal sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; private set; }

        public void SetText(string text) => Text = text;

        public string? GetText() => Text;
    }

    // The commit bar's stand-in: the two setters a write tool drives, and the text they land in.
    internal sealed class FakeCommitEditor : ICommitEditor
    {
        private readonly State<string> _title = new(string.Empty);
        private readonly State<string> _description = new(string.Empty);

        public IReadable<string> Title => _title;
        public IReadable<string> Description => _description;

        public void SetTitle(string value) => _title.Value = value;
        public void SetDescription(string value) => _description.Value = value;
    }

    // Stands in for the OS store so the panel is past onboarding and the environment variable on the
    // machine running the tests cannot change the outcome.
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

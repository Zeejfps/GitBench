using System.Diagnostics;
using System.Text;
using GitBench.App;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// A real branch under review, in a real review-window registry, with the stacked diff list
/// mounted headlessly over the window the registry opened — so a narrator's focus and spotlights
/// run against git's own diffs, the lazy per-file loads, and the drawn rows.
/// </summary>
/// <remarks>
/// The branch changes <c>a.txt</c> in two hunks with a wide unexpanded gap between them, <c>b.txt</c>
/// right below it, and thirty filler files after that — enough stacked sections that the last one
/// sits past the list's load margin and only loads when something asks for it.
/// </remarks>
internal sealed class ReviewPresentationFixture : IDisposable
{
    public const int ALines = 40;
    public const int FillerCount = 30;
    public const int Width = 800;
    public const int Height = 600;

    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir;
    private readonly RepoRegistry _registry;
    private readonly PreferencesService _preferences;
    private GuiTestHarness? _harness;

    public QueuedDispatcher Dispatcher { get; } = new();
    public MessageBus Bus { get; } = new();
    public GitService Git { get; }
    public Repo Repo { get; }
    public ILocalizationService Localization { get; } = new LocalizationService(new State<Locale>(Locale.En));
    public ReviewWindowsViewModel Windows { get; }
    public IRepoRegistry Registry => _registry;

    public GuiTestHarness Harness => _harness ?? throw new InvalidOperationException("Call Mount first.");

    public ReviewPresentationFixture()
    {
        _dir = new TempDir("gitbench-review-presentation-");
        Git = new GitService(new NullActivityTracker());

        RunGit("init", "-q", "--initial-branch=main");
        RunGit("config", "user.email", "test@test");
        RunGit("config", "user.name", "test");
        RunGit("config", "core.autocrlf", "false");
        Write("a.txt", Lines("a line", ALines));
        Write("b.txt", Lines("b line", 10));
        for (var i = 0; i < FillerCount; i++)
            Write(Filler(i), $"filler {i}\n");
        RunGit("add", ".");
        RunGit("-c", "commit.gpgsign=false", "commit", "-q", "-m", "seed the tree");

        RunGit("checkout", "-q", "-b", "feature");
        Write("a.txt", Lines("a line", ALines, changed: [5, 35]));
        Write("b.txt", Lines("b line", 10, changed: [3]));
        for (var i = 0; i < FillerCount; i++)
            Write(Filler(i), $"filler {i}\nfiller {i} changed\n");
        RunGit("add", ".");
        RunGit("-c", "commit.gpgsign=false", "commit", "-q", "-m", "change every file");

        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        if (_registry.Open(_dir.Path) != OpenRepoOutcome.Opened)
            throw new InvalidOperationException("The fixture repository did not open.");
        Repo = _registry.Repos.Single();
        _registry.SetActive(Repo.Id);
        _preferences = new PreferencesService(Preferences.Default, Path.Combine(_dir.Path, "prefs.json"));

        Windows = new ReviewWindowsViewModel(
            Bus,
            new GitReviewStackSource(_registry, Git, Localization),
            _registry,
            Git, Git, Git, Git, Git,
            new UnparsedFiles(),
            new IdleSnapshots(),
            new ReviewProgressStore(),
            Dispatcher,
            Localization,
            _preferences);
    }

    public static string Filler(int i) => $"f{i:00}.txt";

    /// <summary>The write surface a presentation tool hops to the UI thread through.</summary>
    public AssistantWriteSurface Surface() =>
        new(Dispatcher, Bus, _registry, new SilentCommitEditor(), new IdleRemoteOperations(), new TestDocuments.Empty());

    /// <summary>Opens the review of <c>feature</c> against <c>main</c> the way the branch menu
    /// does, waits for its range to resolve, and returns the window.</summary>
    public ReviewWindowViewModel OpenWindow()
    {
        Bus.Broadcast(new OpenReviewWindowMessage(Repo.Id, "feature", "feature", "main", "main"));
        var window = Windows.Windows.Single();
        Pump.WaitFor(Dispatcher, () => window.ContentKind.Value == ReviewContentKind.Loaded, "the review range to load");
        return window;
    }

    /// <summary>Mounts the stacked diff list over a window, as the review window's split does.</summary>
    public GuiTestHarness Mount(ReviewWindowViewModel window)
    {
        _harness = GuiTestHarness.Create(
            ctx => new Provide<IReviewedFileTracker>
            {
                Value = window.ReviewedFiles,
                Child = new Provide<IReviewSurfaceModel>
                {
                    Value = window,
                    Child = new Provide<IReviewPresentationSurface>
                    {
                        Value = window,
                        Child = new Provide<CommitDetailsViewModel>
                        {
                            Value = window.Details,
                            Child = new ReviewDiffPanel(),
                        },
                    },
                },
            }.BuildView(ctx),
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService(new State<ThemeMode>(ThemeMode.Dark));
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(ctx.Require<State<ThemeMode>>()));
                ctx.AddService(Localization);
                ctx.AddService<IUiDispatcher>(Dispatcher);
                ctx.AddService<IRepoRegistry>(_registry);
                ctx.AddService(_preferences);
            });
        Settle();
        return _harness;
    }

    /// <summary>Pumps the dispatcher and draws frames until every file within the load margin has
    /// its diff on screen.</summary>
    public void Settle()
    {
        var sw = Stopwatch.StartNew();
        // A few frames with nothing left queued means the lazy loads near the viewport have landed.
        var quiet = 0;
        while (sw.Elapsed < PumpTimeout && quiet < 5)
        {
            var queued = Dispatcher.Queued;
            Dispatcher.Drain();
            Harness.Render();
            quiet = queued == 0 ? quiet + 1 : 0;
            Thread.Sleep(10);
        }
    }

    /// <summary>Drives a narrator's request to completion: the UI thread here is the test thread,
    /// so the dispatcher is drained and a frame drawn until the list has serviced it.</summary>
    public T Await<T>(Task<T> task)
    {
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < PumpTimeout)
        {
            Dispatcher.Drain();
            _harness?.Render();
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("The request did not complete.");
        return task.GetAwaiter().GetResult();
    }

    public ReviewLineResolution Focus(string path, DiffLineSide side, int line, ReviewWindowViewModel window) =>
        Await(window.FocusLineAsync(new ReviewLineRef(path, side, new FileLine(line)), CancellationToken.None));

    /// <summary>The rows drawn with exactly this text, bottom edge up.</summary>
    public static IReadOnlyList<RectF> TextRows(RecordingCanvas canvas, string text)
    {
        var rows = new List<RectF>();
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text == text) rows.Add(drawn.Inputs.Position);
        return rows;
    }

    public static IReadOnlyList<RectF> RectsOf(RecordingCanvas canvas, uint color)
    {
        var rects = new List<RectF>();
        foreach (var drawn in canvas.Rects)
            if (drawn.Inputs.Style.BackgroundColor == color) rects.Add(drawn.Inputs.Position);
        return rects;
    }

    public static ReviewSpotlightStyles SpotlightStyles => ThemeStyles.Dark.ReviewSpotlight;

    public void Dispose()
    {
        _harness?.Dispose();
        Windows.Dispose();
        _preferences.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private static string Lines(string prefix, int count, int[]? changed = null)
    {
        var text = new StringBuilder();
        for (var n = 1; n <= count; n++)
        {
            text.Append(prefix).Append(' ').Append(n);
            if (changed != null && Array.IndexOf(changed, n) >= 0) text.Append(" changed");
            text.Append('\n');
        }
        return text.ToString();
    }

    private void Write(string path, string text) =>
        File.WriteAllText(Path.Combine(_dir.Path, path), text, new UTF8Encoding(false));

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    // The window's base menu reads the branch listing; nothing here opens it.
    private sealed class IdleSnapshots : IRepoSnapshotStore
    {
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges { get; } = new State<Fetched<LocalChangesData>?>(null);
    }
}

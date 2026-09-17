using System.Collections.Concurrent;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.FileBrowser;
using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Tests;

internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; set; }
    public void SetText(string text) => Text = text;
    public string? GetText() => Text;
}

internal sealed class FakeShell : IPlatformShell
{
    public List<string> OpenedFolders { get; } = [];
    public List<string> OpenedUrls { get; } = [];
    public Exception? OpenUrlThrows { get; set; }

    public void OpenFolder(string path) => OpenedFolders.Add(path);
    public void OpenTerminal(string path) { }
    public void OpenFile(string path) { }

    public void OpenUrl(string url)
    {
        OpenedUrls.Add(url);
        if (OpenUrlThrows is { } e) throw e;
    }
}

internal sealed class NullActivityTracker : IRepoActivityTracker
{
    private sealed class Scope : IDisposable { public void Dispose() { } }
    public IDisposable Begin(string repoPath) => new Scope();
    public bool IsActive(string repoPath) => false;
}

internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

// Runs what was posted only when the test says so, and keeps running until the queue is empty, so
// a continuation that posts another lands in the same drain.
internal sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _posted = new(0);

    public int Queued => _queue.Count;

    public bool Draining { get; private set; }

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        _posted.Release();
    }

    public bool WaitForPost(TimeSpan timeout) => _posted.Wait(timeout);

    public void Drain()
    {
        Draining = true;
        try
        {
            while (_queue.TryDequeue(out var action)) action();
        }
        finally
        {
            Draining = false;
        }
    }
}

internal sealed class NoStatusIngest : IRepoStatusIngest
{
    public int Reserve(Guid repoId) => 0;
    public void Publish(Guid repoId, int reservation, GitStatusSummary? summary) { }
    public void NoteLocalCommit(Guid repoId) { }
}

internal sealed class IdleRemoteOperations : IRepoOperationsStore
{
    private readonly State<RepoOperations> _active = new(RepoOperations.Idle);

    public IReadable<RepoOperations> Active => _active;
    public bool HasUnseenError(Guid repoId) => false;
    public bool IsBusy(Guid repoId) => false;
    public event Action<Repo>? PullDiverged { add { } remove { } }
    public event Action<Repo>? PushRejected { add { } remove { } }
    public void Push(Repo repo, bool force = false) { }
    public void Pull(Repo repo, PullStrategy? strategy = null) { }
    public void Fetch(Repo repo) { }
    public Task<RemoteOpResult> PullAsync(Repo repo, PullStrategy? strategy = null) => Task.FromResult(RemoteOpResult.Ok);
    public Task<RemoteOpResult> FetchAsync(Repo repo) => Task.FromResult(RemoteOpResult.Ok);
}

internal sealed class FakeSnapshotStore : IRepoSnapshotStore
{
    public State<Fetched<LocalChangesData>?> LocalState { get; } = new(null);
    public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
    public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
    public IReadable<Fetched<LocalChangesData>?> LocalChanges => LocalState;
}

internal sealed class FakeFileSystem : IFileSystemReader
{
    public Dictionary<string, List<FileSystemEntry>> Directories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LinkTargets { get; } = new(StringComparer.Ordinal);
    public List<string> Listed { get; } = [];

    public int ListsUnder(string root) =>
        Listed.Count(path => path.StartsWith(root, StringComparison.Ordinal));

    public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
    {
        Listed.Add(absoluteDirectory);
        return Directories.TryGetValue(absoluteDirectory, out var entries)
            ? new DirectoryListing.Listed(entries)
            : new DirectoryListing.Unavailable("No such directory.");
    }

    public string? ResolveLinkTarget(string absolutePath) =>
        LinkTargets.TryGetValue(absolutePath, out var target) ? target : null;

    public FakeFileSystem With(string directory, params FileSystemEntry[] entries)
    {
        Directories[directory] = [.. entries];
        return this;
    }
}

internal static class FileBrowserFakes
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> NoIgnore(IReadOnlyList<string> relativePaths) => None;

    public static IReadOnlyList<string> EmptyCatalog() => [];
}

internal sealed class StyledMeasurer : ITextMeasurer
{
    public float MeasureTextWidth(ReadOnlySpan<char> text, TextStyle style) => text.Length * AdvanceOf(style);

    public float MeasureTextPrefix(ReadOnlySpan<char> text, int prefixLength, TextStyle style) =>
        Math.Clamp(prefixLength, 0, text.Length) * AdvanceOf(style);

    public float MeasureTextLineHeight(TextStyle style) =>
        style.FontSize.IsSet ? style.FontSize.Value : 16f;

    private static float AdvanceOf(TextStyle style) =>
        style.FontWeight is { IsSet: true, Value: FontWeight.Bold } ? 12f : 8f;
}

internal sealed class FakeSecretStore : ISecretStore
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

internal sealed class FakeBus : IMessageBus
{
    public void Broadcast<T>(T message = default) where T : struct { }
    public void Subscribe<T>(Action<T> handler) where T : struct { }
    public void Unsubscribe<T>(Action<T> handler) where T : struct { }
}

internal sealed class SettledHead : IRepoHeadStore, IRepoHeadConfirm
{
    public RepoHead For(Guid repoId) => RepoHead.Settled;
    public void Checkout(Repo repo, string branchName) { }
    public void RunMove(Repo repo, string branchName, Func<GitOutcome> work, string? failureTitle = null) { }
    public Action<bool> BeginMove(Repo repo, string branchName) => _ => { };
    public void Confirm(Guid repoId) { }
}

internal sealed class NoConflicts : IGitConflictOperations
{
    public ConflictContext? GetConflictContext(Repo repo, string path) => null;
    public GitOutcome TakeOurs(Repo repo, string path) => throw new NotSupportedException();
    public GitOutcome TakeTheirs(Repo repo, string path) => throw new NotSupportedException();
    public GitOutcome TakeBoth(Repo repo, string path) => throw new NotSupportedException();
    public GitOutcome MarkResolved(Repo repo, string path) => throw new NotSupportedException();
    public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo) => throw new NotSupportedException();
    public ConflictStages? GetConflictStages(Repo repo, string path) => throw new NotSupportedException();
}

internal sealed class KeyProbe : KeyboardMouseController
{
    public readonly List<KeyboardKey> Seen = new();

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State == InputState.Pressed) Seen.Add(e.Key);
    }
}

internal sealed class FakeNavigator : IFileNavigator
{
    public List<(string Path, int Line)> Went { get; } = [];

    public void NavigateTo(string absolutePath, int line) => Went.Add((absolutePath, line));
}

internal sealed class StubReader : IGitRawConfigReader
{
    public bool IsRepoAvailable(string repoPath) => true;
    public IReadOnlyList<string> GetRemoteNamesRaw(string repoPath) => Array.Empty<string>();
    public string? GetRemoteUrlRaw(string repoPath, string remoteName) => null;
    public (string? Name, string? Email) GetLocalIdentityRaw(string repoPath) => (null, null);
    public void AttachIdentityResolver(GitIdentityService identity) { }
}

internal sealed class ManualTicker : IFrameTicker
{
    private readonly List<Action<float>> _ticks = new();

    public void Add(Action<float> tick) => _ticks.Add(tick);
    public void Remove(Action<float> tick) => _ticks.Remove(tick);

    public void Tick()
    {
        foreach (var tick in _ticks.ToArray()) tick(1f / 60f);
    }
}

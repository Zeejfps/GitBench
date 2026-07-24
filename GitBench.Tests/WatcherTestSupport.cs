using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Tests;

// Shared scaffolding for the filesystem-watcher and reconcile tests. Both drive real components
// whose broadcasts arrive through IUiDispatcher, so the test thread decides when they land.
internal sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public void Drain()
    {
        while (_queue.TryDequeue(out var action)) action();
    }
}

// Stands in for "a git process is running on this repo right now". Flipping Active is how a test
// opens and closes the gate RepoWatcher consults.
internal sealed class GateTracker : IRepoActivityTracker
{
    private volatile bool _active;

    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public IDisposable Begin(string repoPath) => new Scope();

    public bool IsActive(string repoPath) => _active;

    private sealed class Scope : IDisposable
    {
        public void Dispose() { }
    }
}

// Counts every channel the watcher can broadcast on, so a test can assert both what fired and
// what stayed silent.
internal sealed class ChannelRecorder
{
    public int WorkingTree;
    public int Refs;
    public int Worktrees;
    public int Submodules;
    public readonly List<Guid> WorkingTreeRepoIds = new();
    public readonly List<Guid> RefsRepoIds = new();

    public ChannelRecorder(IMessageBus bus)
    {
        bus.Subscribe<WorkingTreeChangedMessage>(m => { WorkingTree++; WorkingTreeRepoIds.Add(m.RepoId); });
        bus.Subscribe<RefsChangedMessage>(m => { Refs++; RefsRepoIds.Add(m.RepoId); });
        bus.Subscribe<WorktreesChangedMessage>(_ => Worktrees++);
        bus.Subscribe<SubmodulesChangedMessage>(_ => Submodules++);
    }

    public int Total => WorkingTree + Refs + Worktrees + Submodules;
}

internal static class Pump
{
    // The watcher's debounce is 250ms and a deferred drain re-arms at that same granularity, so
    // anything shorter than a few cycles is not "settled".
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    // Drains until the condition holds, failing loudly rather than silently asserting on a
    // half-pumped queue.
    public static void WaitFor(QueuedDispatcher dispatcher, Func<bool> done, string what, int timeoutSeconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(10);
        }
        dispatcher.Drain();
        if (done()) return;
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    // Keeps pumping for a fixed window so a test can assert that nothing arrived, or that nothing
    // further arrived after the one broadcast it expected.
    public static void DrainFor(QueuedDispatcher dispatcher, TimeSpan window)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window)
        {
            dispatcher.Drain();
            Thread.Sleep(10);
        }
        dispatcher.Drain();
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { ForceDelete(new DirectoryInfo(Path)); }
        catch { /* best effort: a leftover temp dir is harmless */ }
    }

    // git marks loose objects read-only, which trips Directory.Delete on Windows; clear attributes
    // depth-first before removing.
    public static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}

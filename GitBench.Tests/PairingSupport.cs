using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Pairing;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>Stands in for the editor: answers stops as the test scripts and records what it was
/// asked.</summary>
internal sealed class RecordingPairingPresentation : IPairingPresentation
{
    public List<string> Calls { get; } = new();

    public State<EditorCaret?> CaretState { get; } = new(null);

    public Func<StopTarget, StopPlacement> Answer { get; set; } = target =>
        new StopPlacement.Placed(new StopLocation.OnSymbol("C:/repo/" + target.Path, TextPosition.At(10, 4), "void " + target.Symbol + "()"));

    public List<string> SaveProblems { get; } = new();

    public IReadable<EditorCaret?> Caret => CaretState;

    public Task<StopPlacement> LocateAsync(StopTarget target, CancellationToken ct)
    {
        Calls.Add($"show {target.Path}#{target.Symbol}");
        return Task.FromResult(Answer(target));
    }

    public void Reveal(StopLocation location) => Calls.Add("reveal");

    public bool ShowFile(string relativePath, int line)
    {
        Calls.Add($"show file {relativePath}:{line}");
        return relativePath != "missing.cs";
    }

    public List<(IReadOnlyList<LineSpan> Spotlights, PairingHint? Code)> Hinted { get; } = new();

    public void ShowHints(StopLocation location, IReadOnlyList<LineSpan> spotlights, PairingHint? code) =>
        Hinted.Add((spotlights, code));

    public void ClearHints() => Calls.Add("clear hints");

    public void TakeDraft(StopLocation location) => Calls.Add("take draft");

    public IReadOnlyList<string> SaveUnsaved()
    {
        Calls.Add("save");
        return SaveProblems;
    }

    public string? Relative(string absolutePath) =>
        absolutePath.StartsWith("C:/repo/", StringComparison.Ordinal) ? absolutePath["C:/repo/".Length..] : null;
}

/// <summary>A working tree the test moves by hand: each capture is the tree named by
/// <see cref="Current"/>, and a diff between two names is scripted.</summary>
internal sealed class ScriptedWorkspace : IPairingWorkspace
{
    public string Current { get; set; } = "t0";

    public Dictionary<(string, string), string> Diffs { get; } = new();

    public string? CaptureFailure { get; set; }

    public int Captures { get; private set; }

    public Task<SnapshotResult<TreeSnapshot>> CaptureAsync(CancellationToken ct)
    {
        Captures++;
        return Task.Run<SnapshotResult<TreeSnapshot>>(() => CaptureFailure is { } reason
            ? new SnapshotResult<TreeSnapshot>.Failed(reason)
            : new SnapshotResult<TreeSnapshot>.Ok(new TreeSnapshot(Current)));
    }

    public Task<SnapshotResult<string>> DiffAsync(TreeSnapshot from, TreeSnapshot to, CancellationToken ct) =>
        Task.Run<SnapshotResult<string>>(() => new SnapshotResult<string>.Ok(
            from == to ? string.Empty : Diffs.GetValueOrDefault((from.TreeId, to.TreeId), $"diff {from.TreeId}..{to.TreeId}")));

    public TestCommand? TestCommand { get; set; }

    public void SaveTestCommand(TestCommand command) => TestCommand = command;

    /// <summary>Test files as the fake working tree holds them.</summary>
    public Dictionary<string, string> Files { get; } = new();

    /// <summary>What the next runs answer, in order; the last one repeats.</summary>
    public Queue<TestRun> Runs { get; } = new();

    public List<string> RunNames { get; } = new();

    public Task<TestWrite> WriteTestAsync(string relativePath, string content, CancellationToken ct) => Task.Run<TestWrite>(() =>
    {
        var prior = Files.GetValueOrDefault(relativePath);
        Files[relativePath] = content;
        return new TestWrite.Written(new TestFileUndo(relativePath, prior is null ? null : System.Text.Encoding.UTF8.GetBytes(prior)));
    });

    public Task RestoreAsync(TestFileUndo undo, CancellationToken ct) => Task.Run(() =>
    {
        if (undo.PriorContent is { } prior) Files[undo.RelativePath] = System.Text.Encoding.UTF8.GetString(prior);
        else Files.Remove(undo.RelativePath);
    });

    public Task<TestRun> RunTestAsync(TestCommand command, string name, CancellationToken ct) => Task.Run(() =>
    {
        lock (RunNames) RunNames.Add(name);
        return Runs.Count > 1 ? Runs.Dequeue() : Runs.Peek();
    });
}

/// <summary>No repository has a pairing session.</summary>
internal sealed class NoPairingSessions : IPairingSessions
{
    public PairingStore? StoreFor(Guid repoId) => null;
}

/// <summary>Pairing sessions the test puts in place by hand.</summary>
internal sealed class FixedPairingSessions : IPairingSessions
{
    public Dictionary<Guid, PairingStore> Stores { get; } = new();

    public PairingStore? StoreFor(Guid repoId) => Stores.GetValueOrDefault(repoId);
}

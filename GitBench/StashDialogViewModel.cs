using ZGF.Observable;

namespace GitGui;

internal readonly record struct StashRequest(Repo Repo);

internal sealed record StashFileRow(string Path, FileChange Display, bool IsUntracked);

internal sealed class StashDialogViewModel : ViewModelBase<StashDialogState>
{
    private readonly Repo _repo;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly HashSet<string> _untrackedPaths = new();

    public IReadable<IReadOnlyList<StashFileRow>> Files { get; }
    public IReadable<IReadOnlySet<string>> CheckedPaths { get; }
    public IReadable<string> FilesHeader { get; }
    public IReadable<string> Message { get; }
    public IReadable<bool> KeepStaged { get; }
    public AsyncCommand Stash { get; }

    public event Action? CloseRequested;
    public event Action? FocusMessageRequested;

    public StashDialogViewModel(
        StashRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        LocalChangesSelectionStore selectionStore)
        : base(dispatcher, StashDialogState.Initial)
    {
        _repo = request.Repo;
        _gitService = gitService;
        _bus = bus;

        var snapshot = _gitService.GetLocalChanges(_repo);
        var rows = BuildRows(snapshot, _untrackedPaths);
        var preChecked = ComputePreChecked(rows, selectionStore.UnstagedPaths.Value);
        Update(s => s with
        {
            Files = rows,
            CheckedPaths = new HashSet<string>(preChecked),
        });

        Files = Slice(s => s.Files);
        CheckedPaths = Slice(s => s.CheckedPaths);
        FilesHeader = Slice(s => s.Files.Count == 0
            ? "Files"
            : $"Files ({s.CheckedPaths.Count}/{s.Files.Count})");
        Message = Slice(s => s.Message);
        KeepStaged = Slice(s => s.KeepStaged);

        var canStash = Slice(s => s.Message.Length > 0 && s.CheckedPaths.Count > 0);
        Stash = new AsyncCommand(dispatcher, DoStash, OnStashSucceeded, canStash);
    }

    public void SetMessage(string message) =>
        Update(s => s.Message == message ? s : s with { Message = message });

    public void SetKeepStaged(bool value) =>
        Update(s => s.KeepStaged == value ? s : s with { KeepStaged = value });

    public void ToggleFile(string path) =>
        Update(s =>
        {
            var next = new HashSet<string>(s.CheckedPaths);
            if (!next.Add(path)) next.Remove(path);
            return s with { CheckedPaths = next };
        });

    public void RequestFocusMessage() => FocusMessageRequested?.Invoke();

    private string? DoStash()
    {
        var state = State.Value;
        var paths = new List<string>(state.CheckedPaths.Count);
        var includeUntracked = false;
        foreach (var f in state.Files)
        {
            if (!state.CheckedPaths.Contains(f.Path)) continue;
            paths.Add(f.Path);
            if (_untrackedPaths.Contains(f.Path)) includeUntracked = true;
        }

        var outcome = _gitService.CreateStash(_repo, state.Message, includeUntracked, state.KeepStaged, paths);
        return outcome.Success ? null : (outcome.ErrorMessage ?? "Stash failed.");
    }

    private void OnStashSucceeded()
    {
        _bus.Broadcast(new RefsChangedMessage(_repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(_repo.Id));
        CloseRequested?.Invoke();
    }

    private static IReadOnlyList<StashFileRow> BuildRows(LocalChangesSnapshot snapshot, HashSet<string> untracked)
    {
        untracked.Clear();
        var seen = new Dictionary<string, StashFileRow>(snapshot.Staged.Count + snapshot.Unstaged.Count);
        // Unstaged first so the worktree status wins the display when a path appears on both sides.
        foreach (var f in snapshot.Unstaged)
        {
            var isUntracked = f.Status == FileChangeStatus.Added;
            if (isUntracked) untracked.Add(f.Path);
            seen[f.Path] = new StashFileRow(f.Path, f, isUntracked);
        }
        foreach (var f in snapshot.Staged)
        {
            if (!seen.ContainsKey(f.Path))
                seen[f.Path] = new StashFileRow(f.Path, f, false);
        }
        var rows = seen.Values.ToList();
        rows.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static IReadOnlyList<string> ComputePreChecked(IReadOnlyList<StashFileRow> rows, IReadOnlyList<string> unstagedSelection)
    {
        if (unstagedSelection.Count == 0)
            return rows.Select(r => r.Path).ToList();

        var selSet = new HashSet<string>(unstagedSelection);
        var preChecked = new List<string>(unstagedSelection.Count);
        foreach (var r in rows)
            if (selSet.Contains(r.Path)) preChecked.Add(r.Path);
        return preChecked;
    }
}

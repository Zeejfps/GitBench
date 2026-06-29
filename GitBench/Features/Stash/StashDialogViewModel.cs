using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Stash;

internal readonly record struct StashRequest(Repo Repo);

internal sealed record StashFileRow(string Path, FileChange Display, bool IsUntracked);

internal sealed class StashDialogViewModel : ViewModelBase<StashDialogState>, IDialogViewModel
{
    private readonly Repo _repo;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly HashSet<string> _untrackedPaths = new();
    private readonly string _doneToast;

    public IReadable<IReadOnlyList<StashFileRow>> Files { get; }
    public IReadable<IReadOnlySet<string>> CheckedPaths { get; }
    public IReadable<string> FilesHeader { get; }
    public IReadable<string> Message { get; }
    public IReadable<bool> KeepStaged { get; }
    public AsyncCommand Stash { get; }

    public event Action? CloseRequested;
    public event Action? FocusMessageRequested;

    // The pivot a Shift-click extends a range from; moves to the row of every plain/toggle click.
    private int _anchorIndex = -1;

    public StashDialogViewModel(
        StashRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        LocalChangesSelectionStore selectionStore,
        ILocalizationService loc)
        : base(dispatcher, StashDialogState.Initial)
    {
        _repo = request.Repo;
        _gitService = gitService;
        _bus = bus;
        var strings = loc.Strings.Value;
        _doneToast = strings.ToastChangesStashed;

        var snapshot = _gitService.GetLocalChanges(_repo) is Fetched<LocalChangesSnapshot>.Ok ok
            ? ok.Value
            : new LocalChangesSnapshot(_repo.Id, Array.Empty<FileChange>(), Array.Empty<FileChange>());
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
            ? strings.LocalchangesFilesHeaderEmpty
            : strings.LocalchangesFilesHeader(s.CheckedPaths.Count, s.Files.Count));
        Message = Slice(s => s.Message);
        KeepStaged = Slice(s => s.KeepStaged);

        var canStash = Slice(s => s.Message.Length > 0 && s.CheckedPaths.Count > 0);
        Stash = AsyncCommand.ForOutcome(dispatcher, DoStash, OnStashSucceeded, canStash);
    }

    public void SetMessage(string message) =>
        Update(s => s.Message == message ? s : s with { Message = message });

    public void SetKeepStaged(bool value) =>
        Update(s => s.KeepStaged == value ? s : s with { KeepStaged = value });

    /// <summary>
    /// Handles a click on the row at <paramref name="index"/>. Shift extends a range from the anchor,
    /// checking every row between it and the clicked row (the anchor stays put for further extends);
    /// any other click toggles just that row and moves the anchor to it.
    /// </summary>
    public void ClickRow(int index, InputModifiers modifiers)
    {
        var files = State.Value.Files;
        if ((uint)index >= (uint)files.Count) return;

        if ((modifiers & InputModifiers.Shift) != 0 && (uint)_anchorIndex < (uint)files.Count)
        {
            var lo = Math.Min(_anchorIndex, index);
            var hi = Math.Max(_anchorIndex, index);
            Update(s =>
            {
                var next = new HashSet<string>(s.CheckedPaths);
                for (var i = lo; i <= hi; i++) next.Add(s.Files[i].Path);
                return s with { CheckedPaths = next };
            });
            return;
        }

        _anchorIndex = index;
        var path = files[index].Path;
        Update(s =>
        {
            var next = new HashSet<string>(s.CheckedPaths);
            if (!next.Add(path)) next.Remove(path);
            return s with { CheckedPaths = next };
        });
    }

    public void RequestFocusMessage() => FocusMessageRequested?.Invoke();

    private GitOutcome DoStash()
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

        return _gitService.CreateStash(_repo, state.Message, includeUntracked, state.KeepStaged, paths);
    }

    private void OnStashSucceeded()
    {
        _bus.Broadcast(new RefsChangedMessage(_repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(_repo.Id));
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(_doneToast)));
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

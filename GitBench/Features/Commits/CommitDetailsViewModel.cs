using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

public abstract record CommitDetailsRenderState
{
    // No-selection and error states (carry centered text); Loading shows the structural skeleton.
    public sealed record Placeholder(string Text) : CommitDetailsRenderState;
    public sealed record Loading : CommitDetailsRenderState;
    public sealed record Loaded(CommitDetails Details) : CommitDetailsRenderState;
}

internal sealed record CommitDetailsState(
    CommitDetailsRenderState Render,
    string? SelectedPath,
    FileViewMode ViewMode,
    IReadOnlySet<string> Collapsed,
    string? CursorFolder);

internal sealed class CommitDetailsViewModel : ViewModelBase<CommitDetailsState>
{
    private readonly IGitService _gitService;
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private string? _currentSha;
    // Non-null only in the Review window's Combined mode: the range base. When set, opened files are
    // range tabs (base→head) rather than commit-vs-parent. Null for every commit/History selection.
    private string? _currentBaseSha;
    private Guid _currentRepoId;
    // Set only by ShowWorkingTree: opened files diff HEAD→disk and the file list is pushed in by the
    // host rather than loaded from a sha.
    private bool _workingTree;

    private string DefaultPlaceholder => _loc.Strings.Value.CommitsDetailsNoSelection;

    public IReadable<CommitDetailsRenderState> RenderState { get; }
    public IReadable<string?> SelectedPath { get; }
    public IReadable<FileViewMode> ViewMode { get; }
    public IReadable<IReadOnlySet<string>> CollapsedFolders { get; }

    /// <summary>The folder row the keyboard cursor sits on, or null when it is on a file. Only one of
    /// the two is ever the cursor, so the file tree highlights exactly one row.</summary>
    public IReadable<string?> CursorFolder { get; }
    public Command ToggleViewMode { get; }

    // Open file tabs, in tab order. The "Details" tab (commit metadata + file list) is implicit and
    // always present as the leftmost tab; SelectedPath == null means it is the active tab. Each entry
    // owns its own DiffViewModel, so switching tabs preserves each file's loaded diff and scroll.
    public ObservableList<CommitFileTab> OpenTabs { get; } = new();

    public CommitDetailsViewModel(
        IGitService gitService,
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        PreferencesService preferences,
        bool subscribeToSelection = true)
        : base(dispatcher, new CommitDetailsState(
            new CommitDetailsRenderState.Placeholder(loc.Strings.Value.CommitsDetailsNoSelection),
            null, preferences.Current.FileViewMode, EmptyCollapsed, null))
    {
        _gitService = gitService;
        _registry = registry;
        _bus = bus;
        _loc = loc;
        _preferences = preferences;

        RenderState = Slice(s => s.Render);
        SelectedPath = Slice(s => s.SelectedPath);
        ViewMode = Slice(s => s.ViewMode);
        CollapsedFolders = Slice(s => s.Collapsed);
        CursorFolder = Slice(s => s.CursorFolder);
        ToggleViewMode = new Command(DoToggleViewMode);

        // The History pane follows commit selection over the bus; the Review window drives its own
        // instance directly via Show()/Clear() and opts out so it never reacts to History's selection.
        if (subscribeToSelection)
            Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
    }

    /// <summary>The active tab's diff view model, or null when the implicit Details tab is active.</summary>
    public DiffViewModel? ActiveDiff => FindTab(State.Value.SelectedPath)?.Diff;

    // Shares the global tree/flat preference with the Local Changes panels so the choice is
    // consistent app-wide and persists across launches.
    private void DoToggleViewMode()
    {
        var next = State.Value.ViewMode == FileViewMode.Flat ? FileViewMode.Tree : FileViewMode.Flat;
        _preferences.SetFileViewMode(next);
        Update(s => s with { ViewMode = next });
    }

    /// <summary>Flips a folder's collapsed state (tree mode only).</summary>
    public void ToggleFolder(string folderPath)
    {
        Update(s =>
        {
            var next = new HashSet<string>(s.Collapsed);
            if (!next.Remove(folderPath)) next.Add(folderPath);
            return s with { Collapsed = next };
        });
    }

    /// <summary>Expands every folder by clearing the collapsed set (tree mode only).</summary>
    public void ExpandAllFolders()
    {
        Update(s => s.Collapsed.Count == 0 ? s : s with { Collapsed = EmptyCollapsed });
    }

    /// <summary>
    /// Collapses every folder (tree mode only). The full folder set is derived from the file list
    /// with an empty collapsed set so nested folders aren't hidden from the walk.
    /// </summary>
    public void CollapseAllFolders()
    {
        Update(s =>
        {
            if (s.Render is not CommitDetailsRenderState.Loaded loaded) return s;
            var allRows = FileTreeBuilder.BuildRows(loaded.Details.Files, DiffSide.Commit, FileViewMode.Tree, EmptyCollapsed);
            var folders = new HashSet<string>();
            foreach (var row in allRows)
                if (row.Kind == FileRowKind.Folder) folders.Add(row.FullPath);
            return folders.Count == 0 ? s : s with { Collapsed = folders };
        });
    }

    /// <summary>
    /// Expands or collapses one folder and everything nested under it, leaving the rest of the tree
    /// alone — the folder row's own Expand All / Collapse All, matching the branches tree. Collapsing
    /// walks the file list with an empty collapsed set so subfolders already hidden are still caught.
    /// </summary>
    public void SetFolderSubtreeCollapsed(string folderPath, bool collapsed)
    {
        Update(s =>
        {
            var prefix = folderPath + "/";
            var next = new HashSet<string>(s.Collapsed);
            var changed = false;

            if (collapsed)
            {
                if (s.Render is not CommitDetailsRenderState.Loaded loaded) return s;
                var allRows = FileTreeBuilder.BuildRows(loaded.Details.Files, DiffSide.Commit, FileViewMode.Tree, EmptyCollapsed);
                foreach (var row in allRows)
                {
                    if (row.Kind != FileRowKind.Folder) continue;
                    if (row.FullPath != folderPath && !row.FullPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    changed |= next.Add(row.FullPath);
                }
            }
            else
            {
                changed = next.Remove(folderPath);
                foreach (var path in s.Collapsed)
                    if (path.StartsWith(prefix, StringComparison.Ordinal)) changed |= next.Remove(path);
            }

            return changed ? s with { Collapsed = next } : s;
        });
    }

    /// <summary>Parks the keyboard cursor on a folder row, or hands it back to the selected file
    /// (null). Set by an arrow key stepping onto a folder and by a folder click.</summary>
    public void SetCursorFolder(string? folderPath)
        => Update(s => s.CursorFolder == folderPath ? s : s with { CursorFolder = folderPath });

    /// <summary>
    /// Expands (<paramref name="expand"/> true) or collapses the folder the cursor sits on. No-op
    /// when the cursor is on a file or the folder is already in that state. Wired to Right / Left.
    /// </summary>
    public void SetCursorFolderExpanded(bool expand)
    {
        Update(s =>
        {
            if (s.CursorFolder is not { } folder) return s;
            var isCollapsed = s.Collapsed.Contains(folder);
            if (expand != isCollapsed) return s;
            var next = new HashSet<string>(s.Collapsed);
            if (expand) next.Remove(folder); else next.Add(folder);
            return s with { Collapsed = next };
        });
    }

    private static readonly IReadOnlySet<string> EmptyCollapsed = new HashSet<string>();

    public override void Dispose()
    {
        CloseAllTabs();
        base.Dispose();
    }

    /// <summary>Opens the file in a new tab — or focuses its existing tab — and makes it active.</summary>
    public void SelectFile(string path)
    {
        if (string.IsNullOrEmpty(_currentSha)) return;
        if (FindTab(path) == null)
            OpenTabs.Add(new CommitFileTab(path, _currentSha, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc, _currentBaseSha));
        Update(s => s with { SelectedPath = path });
    }

    /// <summary>
    /// Creates a standalone diff handle for a file of the current commit/range — the same target
    /// resolution an open tab gets, but never added to <see cref="OpenTabs"/>. The review window's
    /// stacked diff list creates these lazily per file; the caller owns disposal. Null when no
    /// commit/range is loaded.
    /// </summary>
    public CommitFileTab? CreateFileDiff(string path)
    {
        if (_workingTree)
            return CommitFileTab.ForWorkingTree(path, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc);
        if (string.IsNullOrEmpty(_currentSha)) return null;
        return new CommitFileTab(path, _currentSha, _currentRepoId, _registry, _gitService, Dispatcher, _bus, _loc, _currentBaseSha);
    }

    /// <summary>Switches the active tab. A null path activates the implicit Details tab.</summary>
    public void ActivateTab(string? path) => Update(s => s with { SelectedPath = path });

    /// <summary>
    /// Enters the loading state immediately, before the next range is resolved — the Review window's
    /// optimistic base switch flips its columns to their loading treatment the instant the reviewer
    /// picks a new base, without waiting for the range resolution to return. A following
    /// <see cref="ShowRange"/> supplies the resolved endpoints.
    /// </summary>
    public void EnterLoading() =>
        Update(s => s with { SelectedPath = null, Render = new CommitDetailsRenderState.Loading() });

    /// <summary>Closes a file tab and disposes its diff. If it was active, the neighbouring tab (or
    /// the Details tab when none remain) becomes active.</summary>
    public void CloseTab(string path)
    {
        var index = IndexOfTab(path);
        if (index < 0) return;
        var tab = OpenTabs[index];
        var wasActive = State.Value.SelectedPath == path;
        OpenTabs.RemoveAt(index);
        tab.Dispose();
        if (!wasActive) return;
        var next = OpenTabs.Count == 0 ? null : OpenTabs[Math.Min(index, OpenTabs.Count - 1)].Path;
        Update(s => s with { SelectedPath = next });
    }

    private CommitFileTab? FindTab(string? path)
    {
        if (path == null) return null;
        foreach (var tab in OpenTabs)
            if (tab.Path == path) return tab;
        return null;
    }

    private int IndexOfTab(string path)
    {
        for (var i = 0; i < OpenTabs.Count; i++)
            if (OpenTabs[i].Path == path) return i;
        return -1;
    }

    private void CloseAllTabs()
    {
        if (OpenTabs.Count == 0) return;
        foreach (var tab in OpenTabs) tab.Dispose();
        OpenTabs.Clear();
    }

    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Sha))
        {
            Clear();
            return;
        }
        // The History pane follows only the active repo's selection — guard stale messages from a
        // repo it has since switched away from.
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        Show(msg.RepoId, msg.Sha);
    }

    /// <summary>Resets to the no-selection placeholder, closing all tabs and cancelling any in-flight load.</summary>
    public void Clear()
    {
        Gen.Bump();
        _currentSha = null;
        _currentBaseSha = null;
        _workingTree = false;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            CursorFolder = null,
            Render = new CommitDetailsRenderState.Placeholder(DefaultPlaceholder),
        });
    }

    /// <summary>
    /// Loads and shows the given commit's details. The repo is resolved by id (not assumed active),
    /// so a pinned surface like the Review window keeps working when it isn't the main window's
    /// active repo. No-ops when the repo isn't open.
    /// </summary>
    public void Show(Guid repoId, string sha)
    {
        var repo = ResolveRepo(repoId);
        if (repo == null) return;

        _currentSha = sha;
        _currentBaseSha = null;
        _workingTree = false;
        _currentRepoId = repoId;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            CursorFolder = null,
            Render = new CommitDetailsRenderState.Loading(),
        });

        RunBackground<CommitDetailsRenderState>(
            work: () =>
            {
                var fetched = _gitService.LoadDetails(repo, sha);
                if (fetched is Fetched<CommitDetails>.Failed failed)
                    return (new CommitDetailsRenderState.Placeholder(failed.Message), null);

                var details = ((Fetched<CommitDetails>.Ok)fetched).Value;
                var pointerChanges = _gitService.GetSubmodulePointerChanges(repo, sha);
                if (pointerChanges.Count > 0)
                    details = MergePointerChanges(details, pointerChanges);
                return (new CommitDetailsRenderState.Loaded(details), null);
            },
            onResult: (result, error) =>
                Update(s => s with
                {
                    Render = error != null ? new CommitDetailsRenderState.Placeholder(error) : result!,
                }));
    }

    /// <summary>
    /// Loads and shows the combined net diff of a review range — base→head as one file list — for the
    /// Review window's Combined mode. The History pane never calls this. The Details tab renders a
    /// synthesized range summary; opening a file diffs base→head (<see cref="DiffSide.Range"/>).
    /// </summary>
    public void ShowRange(Guid repoId, string baseSha, string headSha)
    {
        var repo = ResolveRepo(repoId);
        if (repo == null) return;

        _currentSha = headSha;
        _currentBaseSha = baseSha;
        _workingTree = false;
        _currentRepoId = repoId;
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
            CursorFolder = null,
            Render = new CommitDetailsRenderState.Loading(),
        });

        RunBackground<CommitDetailsRenderState>(
            work: () =>
            {
                var fetched = _gitService.LoadRangeFiles(repo, baseSha, headSha);
                if (fetched is Fetched<IReadOnlyList<FileChange>>.Failed failed)
                    return (new CommitDetailsRenderState.Placeholder(failed.Message), null);

                var files = ((Fetched<IReadOnlyList<FileChange>>.Ok)fetched).Value;
                return (new CommitDetailsRenderState.Loaded(BuildRangeDetails(repoId, baseSha, headSha, files)), null);
            },
            onResult: (result, error) =>
                Update(s => s with
                {
                    Render = error != null ? new CommitDetailsRenderState.Placeholder(error) : result!,
                }));
    }

    /// <summary>
    /// Shows the working tree's changed files as one list, for the working-tree review surface.
    /// Unlike <see cref="Show"/> / <see cref="ShowRange"/> the files are pushed in by the host (which
    /// already has them from the working-tree snapshot), so this is synchronous — and it deliberately
    /// leaves the open tabs alone: the working tree changes on every editor save, and tearing the
    /// surface down each time would throw away the reviewer's scroll and loaded diffs. Opened files
    /// diff HEAD→disk (<see cref="DiffSide.WorkingTree"/>), so staging never re-targets them.
    /// </summary>
    public void ShowWorkingTree(Guid repoId, IReadOnlyList<FileChange> files)
    {
        if (ResolveRepo(repoId) == null) return;

        // A cross-repo switch has nothing in common with what's on screen; drop the tabs.
        if (!_workingTree || _currentRepoId != repoId) CloseAllTabs();

        _workingTree = true;
        _currentSha = null;
        _currentBaseSha = null;
        _currentRepoId = repoId;

        var s = _loc.Strings.Value;
        var details = new CommitDetails(
            RepoId: repoId,
            Sha: CommitFileTab.WorkingTreeKey(repoId),
            AuthorName: s.ReviewWorkingTreeTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: ["HEAD"],
            Files: files);

        Update(state => state with { Render = new CommitDetailsRenderState.Loaded(details) });
    }

    // A synthetic CommitDetails for a combined range so the shared details view renders it unchanged:
    // a labelled "Combined diff" header, a file-count subject, and base shown as the parent. Sha is
    // the shared "base..head" range key, so the Changes list's per-file Viewed marks land under the
    // same identity the open tabs and diff headers use.
    private CommitDetails BuildRangeDetails(Guid repoId, string baseSha, string headSha, IReadOnlyList<FileChange> files)
    {
        var s = _loc.Strings.Value;
        return new CommitDetails(
            RepoId: repoId,
            Sha: ReviewFileKey.ForRange(baseSha, headSha),
            AuthorName: s.ReviewCombinedDiffTitle,
            AuthorEmail: string.Empty,
            AuthorWhen: default,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: default,
            Message: s.ReviewCombinedSummary(files.Count),
            MessageShort: s.ReviewCombinedSummary(files.Count),
            ParentShas: new[] { baseSha },
            Files: files);
    }

    private Repo? ResolveRepo(Guid repoId)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == repoId) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == repoId) return r;
        return null;
    }

    private static CommitDetails MergePointerChanges(CommitDetails details, IReadOnlyList<SubmodulePointerChange> changes)
    {
        var byPath = new Dictionary<string, SubmodulePointerChange>(StringComparer.Ordinal);
        foreach (var c in changes) byPath[c.Path] = c;

        var newFiles = new List<FileChange>(details.Files.Count + changes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in details.Files)
        {
            if (byPath.TryGetValue(f.Path, out var pc))
            {
                newFiles.Add(new FileChange(f.Path, f.OldPath, FileChangeStatus.Submodule)
                {
                    PointerChange = pc,
                });
                seen.Add(f.Path);
            }
            else
            {
                newFiles.Add(f);
            }
        }
        foreach (var c in changes)
        {
            if (seen.Contains(c.Path)) continue;
            newFiles.Add(new FileChange(c.Path, null, FileChangeStatus.Submodule)
            {
                PointerChange = c,
            });
        }
        return details with { Files = newFiles };
    }
}

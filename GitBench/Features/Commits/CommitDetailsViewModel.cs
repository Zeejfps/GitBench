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
    FileViewMode ViewMode);

internal sealed class CommitDetailsViewModel : ViewModelBase<CommitDetailsState>
{
    private readonly IGitService _gitService;
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private string? _currentSha;

    private string DefaultPlaceholder => _loc.Strings.Value.CommitsDetailsNoSelection;

    public IReadable<CommitDetailsRenderState> RenderState { get; }
    public IReadable<string?> SelectedPath { get; }
    public IReadable<FileViewMode> ViewMode { get; }
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
            null, preferences.Current.FileViewMode))
    {
        _gitService = gitService;
        _registry = registry;
        _bus = bus;
        _loc = loc;
        _preferences = preferences;

        RenderState = Slice(s => s.Render);
        SelectedPath = Slice(s => s.SelectedPath);
        ViewMode = Slice(s => s.ViewMode);
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
            OpenTabs.Add(new CommitFileTab(path, _currentSha, _registry, _gitService, Dispatcher, _bus, _loc));
        Update(s => s with { SelectedPath = path });
    }

    /// <summary>Switches the active tab. A null path activates the implicit Details tab.</summary>
    public void ActivateTab(string? path) => Update(s => s with { SelectedPath = path });

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
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
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
        CloseAllTabs();
        Update(s => s with
        {
            SelectedPath = null,
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

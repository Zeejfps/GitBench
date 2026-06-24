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
    public sealed record Placeholder(string Text) : CommitDetailsRenderState;
    public sealed record Loaded(CommitDetails Details) : CommitDetailsRenderState;
}

internal sealed record CommitDetailsState(
    CommitDetailsRenderState Render,
    string? SelectedPath,
    DiffTarget? SelectedTarget,
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
    private string LoadingPlaceholder => _loc.Strings.Value.CommonLoading;

    public IReadable<CommitDetailsRenderState> RenderState { get; }
    public IReadable<string?> SelectedPath { get; }
    public IReadable<DiffTarget?> SelectedTarget { get; }
    public IReadable<FileViewMode> ViewMode { get; }
    public Command ToggleViewMode { get; }
    public DiffViewModel DiffVm { get; }

    public CommitDetailsViewModel(
        IGitService gitService,
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        PreferencesService preferences)
        : base(dispatcher, new CommitDetailsState(
            new CommitDetailsRenderState.Placeholder(loc.Strings.Value.CommitsDetailsNoSelection),
            null, null, preferences.Current.FileViewMode))
    {
        _gitService = gitService;
        _registry = registry;
        _bus = bus;
        _loc = loc;
        _preferences = preferences;

        RenderState = Slice(s => s.Render);
        SelectedPath = Slice(s => s.SelectedPath);
        SelectedTarget = Slice(s => s.SelectedTarget);
        ViewMode = Slice(s => s.ViewMode);
        ToggleViewMode = new Command(DoToggleViewMode);

        DiffVm = new DiffViewModel(SelectedTarget, registry, gitService, dispatcher, bus, loc: loc);
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
    }

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
        DiffVm.Dispose();
        base.Dispose();
    }

    public void SelectFile(string path)
    {
        if (string.IsNullOrEmpty(_currentSha)) return;
        Update(s => s with
        {
            SelectedPath = path,
            SelectedTarget = new DiffTarget(path, DiffSide.Commit, _currentSha),
        });
    }

    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Sha))
        {
            Gen.Bump();
            _currentSha = null;
            Update(s => s with
            {
                SelectedPath = null,
                SelectedTarget = null,
                Render = new CommitDetailsRenderState.Placeholder(DefaultPlaceholder),
            });
            return;
        }
        StartLoad(msg.RepoId, msg.Sha);
    }

    private void StartLoad(Guid repoId, string sha)
    {
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != repoId) return;

        _currentSha = sha;
        Update(s => s with
        {
            SelectedPath = null,
            SelectedTarget = null,
            Render = new CommitDetailsRenderState.Placeholder(LoadingPlaceholder),
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

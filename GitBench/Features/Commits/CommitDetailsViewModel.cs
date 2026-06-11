using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Infrastructure;
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
    DiffTarget? SelectedTarget);

internal sealed class CommitDetailsViewModel : ViewModelBase<CommitDetailsState>
{
    private const string DefaultPlaceholder = "Select a commit to view details.";
    private const string LoadingPlaceholder = "Loading…";

    private readonly IGitService _gitService;
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private string? _currentSha;

    public IReadable<CommitDetailsRenderState> RenderState { get; }
    public IReadable<string?> SelectedPath { get; }
    public IReadable<DiffTarget?> SelectedTarget { get; }
    public DiffViewModel DiffVm { get; }

    public CommitDetailsViewModel(
        IGitService gitService,
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, new CommitDetailsState(
            new CommitDetailsRenderState.Placeholder(DefaultPlaceholder), null, null))
    {
        _gitService = gitService;
        _registry = registry;
        _bus = bus;

        RenderState = Slice(s => s.Render);
        SelectedPath = Slice(s => s.SelectedPath);
        SelectedTarget = Slice(s => s.SelectedTarget);

        DiffVm = new DiffViewModel(SelectedTarget, registry, gitService, dispatcher, bus);
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
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

    // Up/Down arrow navigation through the loaded file list. Moves the single selection by
    // <paramref name="delta"/> rows, clamped to the list bounds. With nothing selected yet, a
    // Down lands on the first row and an Up on the last so the cursor has a visible start.
    public void MoveSelection(int delta)
    {
        if (State.Value.Render is not CommitDetailsRenderState.Loaded loaded) return;
        var files = loaded.Details.Files;
        if (files.Count == 0) return;

        var current = State.Value.SelectedPath;
        var index = current == null ? -1 : IndexOfPath(files, current);
        var next = ListNavigation.NextIndex(files.Count, index, delta);
        SelectFile(files[next].Path);
    }

    private static int IndexOfPath(IReadOnlyList<FileChange> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
            if (files[i].Path == path) return i;
        return -1;
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

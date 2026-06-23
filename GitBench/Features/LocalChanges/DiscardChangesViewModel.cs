using GitBench.Features.Commits;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed record DiscardFileRow(string Path, FileChange Display);

internal sealed class DiscardChangesViewModel : ViewModelBase<DiscardChangesState>, IDialogViewModel
{
    private readonly Repo _repo;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<IReadOnlyList<DiscardFileRow>> Files { get; }
    public IReadable<IReadOnlySet<string>> CheckedPaths { get; }
    public IReadable<string> FilesHeader { get; }
    public AsyncCommand Discard { get; }

    public event Action? CloseRequested;

    public DiscardChangesViewModel(
        DiscardChangesRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
        : base(dispatcher, DiscardChangesState.Initial)
    {
        _repo = request.Repo;
        _gitService = gitService;
        _bus = bus;
        var strings = loc.Strings.Value;

        var rows = _gitService.GetLocalChanges(_repo) is Fetched<LocalChangesSnapshot>.Ok ok
            ? BuildRows(ok.Value)
            : Array.Empty<DiscardFileRow>();
        var preChecked = ComputePreChecked(rows, request.Paths);
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

        var canDiscard = Slice(s => s.CheckedPaths.Count > 0);
        Discard = AsyncCommand.ForOutcome(dispatcher, DoDiscard, OnDiscardSucceeded, canDiscard);
    }

    public void ToggleFile(string path) =>
        Update(s =>
        {
            var next = new HashSet<string>(s.CheckedPaths);
            if (!next.Add(path)) next.Remove(path);
            return s with { CheckedPaths = next };
        });

    private GitOutcome DoDiscard()
    {
        var state = State.Value;
        var paths = new List<string>(state.CheckedPaths.Count);
        foreach (var f in state.Files)
            if (state.CheckedPaths.Contains(f.Path)) paths.Add(f.Path);

        return _gitService.DiscardChanges(_repo, paths);
    }

    private void OnDiscardSucceeded()
    {
        _bus.Broadcast(new WorkingTreeChangedMessage(_repo.Id));
        CloseRequested?.Invoke();
    }

    private static IReadOnlyList<DiscardFileRow> BuildRows(LocalChangesSnapshot snapshot)
    {
        var rows = new List<DiscardFileRow>(snapshot.Unstaged.Count);
        foreach (var f in snapshot.Unstaged)
            rows.Add(new DiscardFileRow(f.Path, f));
        rows.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static IReadOnlyList<string> ComputePreChecked(IReadOnlyList<DiscardFileRow> rows, IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
            return rows.Select(r => r.Path).ToList();

        var reqSet = new HashSet<string>(requested);
        var preChecked = new List<string>(requested.Count);
        foreach (var r in rows)
            if (reqSet.Contains(r.Path)) preChecked.Add(r.Path);
        return preChecked;
    }
}

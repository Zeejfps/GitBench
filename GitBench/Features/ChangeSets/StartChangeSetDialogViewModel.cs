using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.ChangeSets;

/// <summary>
/// Backs the "Start change set…" dialog (Phase 4.1): a branch-name field plus one row per primary in
/// the group — each a checkbox (include this repo, defaulting on) and a start-point field (defaulting
/// to that repo's own default-branch tip, resolved off-thread). Confirm routes through
/// <see cref="ChangeSetOperations.CreateInAll"/> so the create loop, per-repo outcomes, and summary
/// toast are shared with the other batch ops (Phase 2) — the dialog itself just gathers the selection.
/// </summary>
internal sealed class StartChangeSetDialogViewModel : IDialogViewModel
{
    // One selectable member row. Included defaults on (the checklist defaults to the group's
    // primaries); StartPoint is seeded blank and filled with the repo's default branch by
    // ResolveDefaults — a blank field falls back to HEAD at create time.
    public sealed class RepoRow
    {
        public Guid RepoId { get; }
        public string DisplayName { get; }
        public State<bool> Included { get; } = new(true);
        public State<string> StartPoint { get; } = new(string.Empty);

        public RepoRow(Guid repoId, string displayName)
        {
            RepoId = repoId;
            DisplayName = displayName;
        }

        public void Dispose()
        {
            Included.Dispose();
            StartPoint.Dispose();
        }
    }

    public State<string> Name { get; } = new(string.Empty);
    public IReadOnlyList<RepoRow> Rows { get; }
    public IReadable<FieldStatus?> NameStatus { get; }
    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    private readonly Derived<FieldStatus?> _nameStatus;
    private readonly Derived<bool> _canCreate;
    private bool _disposed;

    public StartChangeSetDialogViewModel(
        IReadOnlyList<Repo> repos,
        ChangeSetOperations ops,
        IGitService git,
        IUiDispatcher dispatcher,
        ILocalizationService loc)
    {
        Rows = repos.Select(r => new RepoRow(r.Id, r.DisplayName)).ToList();

        _nameStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(Name.Value, s, s.RefnameNounBranch);
        });
        NameStatus = _nameStatus;

        // A valid, non-empty name and at least one selected member. Reading each row's Included inside
        // the Derived re-gates the confirm button as checkboxes toggle, no manual subscription needed.
        _canCreate = new Derived<bool>(() =>
            Name.Value.Length > 0 && RefNameRules.IsValid(Name.Value) && Rows.Any(row => row.Included.Value));

        // Fire-and-forget through the coordinator (Phase 2 summary reporting). The empty work exists
        // only to gate the confirm button via _canCreate and give it the standard dialog wiring; the
        // real action runs in onSuccess and CreateInAll does its own off-thread loop + reporting.
        Create = new AsyncCommand(
            dispatcher,
            work: () => null,
            onSuccess: () =>
            {
                var selected = new List<(Guid RepoId, string StartPoint)>(Rows.Count);
                foreach (var row in Rows)
                    if (row.Included.Value) selected.Add((row.RepoId, row.StartPoint.Value));
                ops.CreateInAll(selected, Name.Value.Trim());
                CloseRequested?.Invoke();
            },
            gate: _canCreate);

        ResolveDefaults(repos, git, dispatcher);
    }

    // Resolves each member's default branch off-thread and seeds its start-point field with it, unless
    // the user already typed something in the brief window before it returns. A failed probe leaves the
    // field blank (CreateInAll falls back to HEAD).
    private void ResolveDefaults(IReadOnlyList<Repo> repos, IGitService git, IUiDispatcher dispatcher)
    {
        Task.Run(() =>
        {
            var defaults = new Dictionary<Guid, string>();
            foreach (var repo in repos)
            {
                try
                {
                    if (git.GetDefaultBranchName(repo) is { Length: > 0 } name)
                        defaults[repo.Id] = name;
                }
                catch { /* leave blank → HEAD fallback */ }
            }

            dispatcher.Post(() =>
            {
                if (_disposed) return;
                foreach (var row in Rows)
                    if (row.StartPoint.Value.Length == 0 && defaults.TryGetValue(row.RepoId, out var name))
                        row.StartPoint.Value = name;
            });
        });
    }

    public void Dispose()
    {
        _disposed = true;
        (Create.CanExecute as IDisposable)?.Dispose();
        _canCreate.Dispose();
        _nameStatus.Dispose();
        Name.Dispose();
        foreach (var row in Rows) row.Dispose();
    }
}

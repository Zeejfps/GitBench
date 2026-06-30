using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Review;

// Render phase of a review window: loading the stack, a centered message (empty range / error), or a
// loaded stack ready to step through. Mirrors CommitDetailsRenderState's shape.
internal abstract record ReviewRenderState
{
    public sealed record Loading : ReviewRenderState;
    public sealed record Placeholder(string Text) : ReviewRenderState;
    public sealed record Loaded(ReviewStack Stack) : ReviewRenderState;
}

// What the window body should show, derived from ReviewRenderState for the view's Switch.
internal enum ReviewContentKind { Loading, Message, Loaded }

internal sealed record ReviewState(
    ReviewRenderState Render,
    string? SelectedSha,
    IReadOnlySet<string> ReviewedShas);

/// <summary>
/// One open review window, pinned to a single <see cref="ReviewSession"/>. Loads the stack through
/// <see cref="IReviewStackSource"/>, auto-selects the first (base-most) increment, and drives the
/// reused commit-details surface via its own <see cref="CommitDetailsViewModel"/>. Tracks which
/// increments the reviewer has marked reviewed (ephemeral for the window's lifetime) and offers
/// step-through navigation over the unreviewed ones.
/// </summary>
internal sealed class ReviewWindowViewModel : ViewModelBase<ReviewState>
{
    private const int StackCap = 200;
    private static readonly IReadOnlySet<string> NoneReviewed = new HashSet<string>();

    private readonly IReviewStackSource _source;
    private readonly CommitDetailsViewModel _details;
    private readonly ReviewedFileTracker _reviewedFiles = new();

    public ReviewSession Session { get; }
    public string Title { get; }

    // Per-window store of marked-Viewed files, provided into the window subtree so the reused
    // diff-pane header shows its Viewed toggle. Owned (and disposed) here.
    public IReviewedFileTracker ReviewedFiles => _reviewedFiles;

    // The window's own commit-details VM, provided into the right pane's sub-context. It does not
    // subscribe to commit selection, so the History pane can never drive it. Its lifetime is owned
    // by the reused CommitDetailsView (which disposes it via UseViewModel on unmount), so this VM
    // must not dispose it again — hence no Dispose override here.
    public CommitDetailsViewModel Details => _details;

    public IReadable<ReviewContentKind> ContentKind { get; }
    public IReadable<string> PlaceholderText { get; }
    public IReadable<string?> SelectedSha { get; }
    public IReadable<IReadOnlySet<string>> ReviewedShas { get; }
    public IReadable<IReadOnlyList<ReviewIncrement>> Increments { get; }
    public IReadable<string> RangeText { get; }
    public IReadable<string> IncrementLabel { get; }
    public IReadable<string> ReviewedLabel { get; }
    public IReadable<string> FilesViewedLabel { get; }

    public ReviewWindowViewModel(
        ReviewSession session,
        IReviewStackSource source,
        IUiDispatcher dispatcher,
        CommitDetailsViewModel details)
        : base(dispatcher, new ReviewState(new ReviewRenderState.Loading(), null, NoneReviewed))
    {
        Session = session;
        _source = source;
        _details = details;
        Title = $"Review: {session.HeadLabel}";

        ContentKind = Slice(s => s.Render switch
        {
            ReviewRenderState.Loading => ReviewContentKind.Loading,
            ReviewRenderState.Placeholder => ReviewContentKind.Message,
            _ => ReviewContentKind.Loaded,
        });
        PlaceholderText = Slice(s => s.Render is ReviewRenderState.Placeholder p ? p.Text : string.Empty);
        SelectedSha = Slice(s => s.SelectedSha);
        ReviewedShas = Slice(s => s.ReviewedShas);
        Increments = Slice(s => s.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>());
        RangeText = Slice(BuildRangeText);
        IncrementLabel = Slice(BuildIncrementLabel);
        ReviewedLabel = Slice(BuildReviewedLabel);

        // Combines own state, the selected increment's loaded file list, and the Viewed tracker, so it
        // refreshes on selection, on details load, and on every Viewed toggle.
        var filesViewedLabel = new Derived<string>(BuildFilesViewedLabel);
        FilesViewedLabel = filesViewedLabel;
        Subscriptions.Add(filesViewedLabel);

        // Viewing the last file of the selected increment marks the increment reviewed (one-way:
        // un-viewing a file doesn't revoke the increment's reviewed mark, which can also be set by hand).
        Subscriptions.Add(_reviewedFiles.Revision.Subscribe(_ => MarkIncrementIfAllFilesViewed()));
        Subscriptions.Add(_details.RenderState.Subscribe(_ => MarkIncrementIfAllFilesViewed()));

        StartLoad();
    }

    // Selects an increment and drives the right pane to its commit-vs-parent diff. Dedupes so a
    // re-click of the current row is a no-op.
    public void SelectIncrement(string sha)
    {
        if (State.Value.SelectedSha == sha) return;
        Update(s => s with { SelectedSha = sha });
        _details.Show(Session.RepoId, sha);
    }

    // Flips an increment's reviewed mark. Ephemeral: cleared when the window closes (Phase 5a).
    // Per-file Viewed state and persistence land later.
    public void ToggleReviewed(string sha)
    {
        Update(s =>
        {
            var next = new HashSet<string>(s.ReviewedShas);
            if (!next.Remove(sha)) next.Add(sha);
            return s with { ReviewedShas = next };
        });
    }

    // Marks the selected increment reviewed, then steps to the next increment toward the tip. At the
    // tip the selection stays put (the increment is still marked).
    public void MarkReviewedAndAdvance()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return;
        Update(s => s with { ReviewedShas = new HashSet<string>(s.ReviewedShas) { sha } });

        var list = CurrentIncrements();
        var index = IndexOf(list, sha);
        if (index >= 0 && index + 1 < list.Count)
            SelectIncrement(list[index + 1].Sha);
    }

    // Selects the next unreviewed increment after the current selection, wrapping past the tip back
    // to the base. No-op when every increment is reviewed (or the stack is empty).
    public void NextUnreviewed()
    {
        var list = CurrentIncrements();
        if (list.Count == 0) return;
        var reviewed = State.Value.ReviewedShas;
        var start = IndexOf(list, State.Value.SelectedSha);
        for (var step = 1; step <= list.Count; step++)
        {
            var candidate = list[(start + step) % list.Count];
            if (!reviewed.Contains(candidate.Sha))
            {
                SelectIncrement(candidate.Sha);
                return;
            }
        }
    }

    // Disposes the owned Viewed tracker, then the base (slices/subscriptions). The shared _details VM
    // is owned by the reused CommitDetailsView and must not be disposed here.
    public override void Dispose()
    {
        _reviewedFiles.Dispose();
        base.Dispose();
    }

    private void StartLoad()
    {
        Update(s => s with { Render = new ReviewRenderState.Loading() });
        RunBackground<ReviewStack>(
            // The source is async by contract; the stub completes synchronously, so bridging through
            // RunBackground's worker keeps the proven staleness/dispatcher handling. A truly async
            // git source (Phase 4) can move off this bridge.
            work: () => (_source.LoadAsync(Session, StackCap).GetAwaiter().GetResult(), null),
            onResult: (stack, error) =>
            {
                if (error != null)
                {
                    Update(s => s with { Render = new ReviewRenderState.Placeholder(error) });
                    return;
                }
                if (stack == null || stack.Increments.Count == 0)
                {
                    Update(s => s with
                    {
                        Render = new ReviewRenderState.Placeholder("No commits to review in this range."),
                    });
                    return;
                }

                // Land on the first (base-most) increment so the right pane opens on something.
                // Drive the details VM before flipping to Loaded so the pane mounts already loading.
                var first = stack.Increments[0].Sha;
                _details.Show(Session.RepoId, first);
                Update(s => s with { Render = new ReviewRenderState.Loaded(stack), SelectedSha = first });
            });
    }

    private string BuildRangeText(ReviewState s)
    {
        var baseLabel = s.Render is ReviewRenderState.Loaded l ? l.Stack.BaseLabel : (Session.BaseLabel ?? "auto");
        return $"{baseLabel} → {Session.HeadLabel}";
    }

    private string BuildIncrementLabel(ReviewState s)
    {
        if (s.Render is not ReviewRenderState.Loaded l) return string.Empty;
        var total = l.Stack.Increments.Count;
        if (total == 0 || s.SelectedSha == null) return string.Empty;
        var index = IndexOf(l.Stack.Increments, s.SelectedSha);
        return index < 0 ? string.Empty : $"Increment {index + 1} of {total}";
    }

    private string BuildReviewedLabel(ReviewState s)
    {
        if (s.Render is not ReviewRenderState.Loaded l) return string.Empty;
        return $"{s.ReviewedShas.Count} / {l.Stack.Increments.Count} reviewed";
    }

    // "X / Y files viewed" for the selected increment. Empty until a non-empty increment is loaded.
    private string BuildFilesViewedLabel()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return string.Empty;
        var files = SelectedFiles();
        if (files.Count == 0) return string.Empty;

        _ = _reviewedFiles.Revision.Value;
        var viewed = 0;
        foreach (var f in files)
            if (_reviewedFiles.IsViewed(sha, f.Path)) viewed++;
        return $"{viewed} / {files.Count} files viewed";
    }

    private void MarkIncrementIfAllFilesViewed()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null || State.Value.ReviewedShas.Contains(sha)) return;
        var files = SelectedFiles();
        if (files.Count == 0) return;

        var viewed = _reviewedFiles.ViewedPaths(sha);
        foreach (var f in files)
            if (!viewed.Contains(f.Path)) return;
        Update(s => s with { ReviewedShas = new HashSet<string>(s.ReviewedShas) { sha } });
    }

    // The selected increment's files, available once its details have loaded into the right pane.
    private IReadOnlyList<FileChange> SelectedFiles() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : Array.Empty<FileChange>();

    private IReadOnlyList<ReviewIncrement> CurrentIncrements() =>
        State.Value.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>();

    private static int IndexOf(IReadOnlyList<ReviewIncrement> list, string? sha)
    {
        if (sha == null) return -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Sha == sha) return i;
        return -1;
    }
}

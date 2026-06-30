using GitBench.Features.Commits;
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
/// reused commit-details surface via its own <see cref="CommitDetailsViewModel"/>. Reviewed-state is
/// stubbed empty here; toggling lands in Phase 5.
/// </summary>
internal sealed class ReviewWindowViewModel : ViewModelBase<ReviewState>
{
    private const int StackCap = 200;
    private static readonly IReadOnlySet<string> NoneReviewed = new HashSet<string>();

    private readonly IReviewStackSource _source;
    private readonly CommitDetailsViewModel _details;

    public ReviewSession Session { get; }
    public string Title { get; }

    // The window's own commit-details VM, provided into the right pane's sub-context. It does not
    // subscribe to commit selection, so the History pane can never drive it. Its lifetime is owned
    // by the reused CommitDetailsView (which disposes it via UseViewModel on unmount), so this VM
    // must not dispose it again — hence no Dispose override here.
    public CommitDetailsViewModel Details => _details;

    public IReadable<ReviewContentKind> ContentKind { get; }
    public IReadable<string> PlaceholderText { get; }
    public IReadable<string?> SelectedSha { get; }
    public IReadable<IReadOnlyList<ReviewIncrement>> Increments { get; }
    public IReadable<string> RangeText { get; }
    public IReadable<string> IncrementLabel { get; }
    public IReadable<string> ReviewedLabel { get; }

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
        Increments = Slice(s => s.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>());
        RangeText = Slice(BuildRangeText);
        IncrementLabel = Slice(BuildIncrementLabel);
        ReviewedLabel = Slice(BuildReviewedLabel);

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

    private static int IndexOf(IReadOnlyList<ReviewIncrement> list, string sha)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Sha == sha) return i;
        return -1;
    }
}

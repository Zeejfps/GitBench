using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.Review;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The working-tree pane: the chosen presentation of the working tree — file lists over a diff pane, or
/// every diff stacked in one scroll — above an animated footer that holds the commit bar, or — while an
/// operation is in progress — the merge bar or operation panel in its place. Both layouts stage into the
/// same index and commit through the same bar; the choice lives in the toolbar
/// (<c>WorkingChangesLayoutToggle</c>) and is remembered across launches.
/// </summary>
internal sealed record WorkingChanges : Widget
{
    protected override View CreateView(Context ctx) => new LocalChangesView(ctx);
}

internal sealed class LocalChangesView : ContainerView
{
    // Footer kinds in the order an operation reveals them; 0 = nothing to commit and no operation.
    private const int NoFooter = 0;
    private const int CommitBar = 1;
    private const int MergeBar = 2;
    private const int OperationPanel = 3;

    // Body presentations: file lists, the single-repo review, or the cross-repo "All repos" review.
    private const int ListLayout = 0;
    private const int SingleReview = 1;
    private const int CrossReview = 2;

    public LocalChangesView(Context ctx)
    {
        var vm = ctx.Require<LocalChangesViewModel>();
        var operation = ctx.Require<OperationViewModel>();
        var layout = ctx.Require<State<WorkingChangesLayout>>();
        var scope = ctx.Require<State<ChangeSetPanelScope>>();
        var crossVm = ctx.Require<ChangeSetWorkingTreeReviewViewModel>();
        var content = new LocalChangesContentView(ctx, vm);

        // Tab cycles unstaged files → commit title → commit description → commit button
        // (when enabled) → (back to files). The file list registers first so it leads the
        // cycle; the commit bar appends its own stops the first time the footer builds it.
        var focusRing = new FocusRing();
        content.RegisterFocusStops(focusRing);

        // The body is the file lists, the single-repo review, or — when "All repos" is chosen and the
        // active branch is a synced set with members checked out — the cross-repo review (Phase 5.3).
        // Switching away from such a branch drops crossVm.IsAvailable, so this falls back to the
        // single-repo review with no extra state.
        var bodyMode = new Derived<int>(() =>
        {
            if (layout.Value != WorkingChangesLayout.Review) return ListLayout;
            return scope.Value == ChangeSetPanelScope.AllRepos && crossVm.IsAvailable.Value
                ? CrossReview
                : SingleReview;
        });

        // Keep-alive: the list layout's view owns the focus ring and its panels' scroll state, and the
        // review layouts' stacked diffs are expensive to re-mint. Switching only toggles which is shown.
        var body = new Switch<int>
        {
            Value = bodyMode,
            KeepAlive = true,
            Case = m => m switch
            {
                SingleReview => new WorkingTreeReviewView(),
                CrossReview => new ChangeSetWorkingTreeReviewView(),
                _ => new Raw { View = content },
            },
        }.BuildView(ctx);

        // The footer swaps between the commit bar (no operation), the merge bar (an operation that
        // carries a commit), and the operation panel (the sequencer operations). Each gates its own
        // commit / focus on whether it is the surface currently on screen.
        var normalCommit = new Derived<bool>(() => operation.ShowsCommitBox.Value && !operation.IsActive.Value);
        var mergeActive = new Derived<bool>(() => operation.ShowsCommitBox.Value && operation.IsActive.Value);
        var kind = new Derived<int>(() =>
        {
            // The cross-repo review owns its own commit bar (batch commit across the set), so the shared
            // footer steps aside while it is on screen.
            if (bodyMode.Value == CrossReview) return NoFooter;
            return operation.IsActive.Value
                ? (operation.ShowsCommitBox.Value ? MergeBar : OperationPanel)
                : (operation.ShowsCommitBox.Value ? CommitBar : NoFooter);
        });

        var footer = new FooterSlot
        {
            Kind = kind,
            Content = k => k switch
            {
                CommitBar => new CommitBarWidget { FocusRing = focusRing, Vm = vm, Active = normalCommit },
                MergeBar => new CommitBarWidget { FocusRing = new FocusRing(), Vm = vm, Active = mergeActive, ShowOperationChrome = true },
                _ => new OperationPanelWidget(),
            },
        }.BuildView(ctx);

        // The layout tabs sit above the footer, not inside it: the footer swaps itself out for the
        // merge bar and the operation panel, and the pane's presentation is still switchable then.
        var south = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [new WorkingChangesTabStrip(), new Raw { View = footer }],
        }.BuildView(ctx);

        var bg = new RectView
        {
            Children =
            {
                new BorderLayoutView
                {
                    Center = body,
                    South = south,
                },
            },
        };
        bg.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Surface);
        AddChildToSelf(bg);
    }
}

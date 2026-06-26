using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The working-tree pane: the staged/unstaged file panels and diff above an animated footer that holds
/// the commit bar, or — while an operation is in progress — the merge bar or operation panel in its place.
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

    public LocalChangesView(Context ctx)
    {
        var vm = ctx.Require<LocalChangesViewModel>();
        var operation = ctx.Require<OperationViewModel>();
        var content = new LocalChangesContentView(ctx, vm);

        // Tab cycles unstaged files → commit title → commit description → commit button
        // (when enabled) → (back to files). The file list registers first so it leads the
        // cycle; the commit bar appends its own stops the first time the footer builds it.
        var focusRing = new FocusRing();
        content.RegisterFocusStops(focusRing);

        // The footer swaps between the commit bar (no operation), the merge bar (an operation that
        // carries a commit), and the operation panel (the sequencer operations). Each gates its own
        // commit / focus on whether it is the surface currently on screen.
        var normalCommit = new Derived<bool>(() => operation.ShowsCommitBox.Value && !operation.IsActive.Value);
        var mergeActive = new Derived<bool>(() => operation.ShowsCommitBox.Value && operation.IsActive.Value);
        var kind = new Derived<int>(() => operation.IsActive.Value
            ? (operation.ShowsCommitBox.Value ? MergeBar : OperationPanel)
            : (operation.ShowsCommitBox.Value ? CommitBar : NoFooter));

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

        var bg = new RectView
        {
            Children =
            {
                new BorderLayoutView
                {
                    Center = content,
                    South = footer,
                },
            },
        };
        bg.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Surface);
        AddChildToSelf(bg);
    }
}

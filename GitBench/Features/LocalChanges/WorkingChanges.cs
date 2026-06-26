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
/// The working-tree pane: the staged/unstaged file panels and diff above the commit bar.
/// </summary>
internal sealed record WorkingChanges : Widget
{
    protected override View CreateView(Context ctx) => new LocalChangesView(ctx);
}

internal sealed class LocalChangesView : ContainerView
{
    public LocalChangesView(Context ctx)
    {
        var vm = ctx.Require<LocalChangesViewModel>();
        var operation = ctx.Require<OperationViewModel>();
        var content = new LocalChangesContentView(ctx, vm);

        // Tab cycles unstaged files → commit title → commit description → commit button
        // (when enabled) → (back to files). The file list registers first so it leads the
        // cycle; the commit bar appends its own stops as it builds.
        var focusRing = new FocusRing();
        content.RegisterFocusStops(focusRing);
        // The normal commit bar shows only outside an operation. Merge / unmerged-paths commits move
        // to the workspace footer's merge bar (visible on both tabs), so this one steps aside for them.
        var normalCommit = new Derived<bool>(() => operation.ShowsCommitBox.Value && !operation.IsActive.Value);
        var commitBar = new CommitBarWidget { FocusRing = focusRing, Vm = vm, Active = normalCommit }.BuildView(ctx);

        commitBar.BindIsVisible(normalCommit);
        var commitRegion = new ColumnView();
        commitRegion.Children.Add(commitBar);

        var bg = new RectView
        {
            Children =
            {
                new BorderLayoutView
                {
                    Center = content,
                    South = commitRegion,
                },
            },
        };
        bg.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Surface);
        AddChildToSelf(bg);
    }
}

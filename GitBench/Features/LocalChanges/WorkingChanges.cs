using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

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
        var content = new LocalChangesContentView(ctx, vm);
        var commitBar = new CommitBarView(ctx, vm);

        var bg = new RectView
        {
            Children =
            {
                new BorderLayoutView
                {
                    Center = content,
                    South = commitBar,
                },
            },
        };
        bg.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Surface);
        AddChildToSelf(bg);

        // Tab cycles unstaged files → commit title → commit description → commit button
        // (when enabled) → (back to files).
        var focusRing = new FocusRing();
        content.RegisterFocusStops(focusRing);
        commitBar.RegisterFocusStops(focusRing);
        commitBar.RegisterCommitButtonStop(focusRing);

        this.UseViewModel(() => vm, _ => { });
    }
}

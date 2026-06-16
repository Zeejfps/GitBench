using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.StatusBar;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record AppView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();

        // Shared instance: mounted as a layer below, and handed to the presenter behavior above.
        var dialogSurface = new DialogSurfaceView(input);

        var frame = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                // Full-width update banner; self-hides (collapsing its slot) until an update is
                // staged. Separate from the per-repo operation banner inside the workspace.
                new UpdateBannerView(),
                new Grow
                {
                    Child = new BorderLayout
                    {
                        West = new RepoBarSidebar(),
                        Center = new RepoView(),
                        South = new StatusBarView(),
                    },
                },
            ],
        };

        return new Stack
        {
            Children =
            [
                frame,
                new Raw { View = new DragOverlay(ctx) },
                new Raw { View = dialogSurface },
                // Headless host that materializes pop-out diff windows from DiffWindowsViewModel.
                new Raw { View = new DiffWindowsView(ctx) },
            ],
        }
        .WithBehaviors(new DialogPresenter(ctx, dialogSurface))
        .WithController(input, () => new AppKeybindController(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IMessageBus>()));
    }
}

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

        var frame = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
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
                new DragOverlay(),
                new DialogSurface(),
                new DiffWindowsView(),
            ],
        }
        .WithController(input, () => new AppKeybindController(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IMessageBus>()));
    }
}

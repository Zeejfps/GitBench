using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Features.Notifications;
using GitBench.Features.Review;
using GitBench.Features.StatusBar;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record AppWidget : Widget<AppViewModel>
{
    protected override IWidget Build(Context ctx, AppViewModel vm)
    {
        var frame = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new UpdateBannerView(),
                new Grow
                {
                    // The whole workspace swaps for the full-window welcome screen while no
                    // repositories are open, so first-run isn't a maze of empty panels.
                    Child = new Switch<bool>
                    {
                        Value = vm.HasRepos,
                        Case = has => has
                            ? new BorderLayout
                            {
                                West = new RepoBarSidebar(),
                                Center = new RepoView(),
                                South = new StatusBarView(),
                            }
                            : new WelcomeView(),
                    },
                },
            ],
        };

        var content = new Stack
        {
            Children =
            [
                frame,
                new ToastHostView(),
                new DragOverlay(),
                new DialogSurface(),
                new DiffWindowsView(),
                new ReviewWindowsView(),
            ],
        }
        .WithController<AppKeybindController>(ctx);

        // Establish the UI writing direction for the whole tree from the active locale, so RTL
        // locales (Arabic) mirror Row/Column and swap the BorderLayout sidebar to the right.
        return Direction.Wrap(content);
    }
}

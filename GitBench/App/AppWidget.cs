using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Features.Notifications;
using GitBench.Features.Review;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record AppWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var content = new Stack
        {
            Children =
            [
                new AppContentWidget(),
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

using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Features.Notifications;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record AppView : Widget
{
    protected override IWidget Build(Context ctx)
    {
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

        var content = new Stack
        {
            Children =
            [
                frame,
                new ToastHostView(),
                new DragOverlay(),
                new DialogSurface(),
                new DiffWindowsView(),
            ],
        }
        .WithController<AppKeybindController>(ctx);

        // Establish the UI writing direction for the whole tree from the active locale, so RTL
        // locales (Arabic) mirror Row/Column and swap the BorderLayout sidebar to the right.
        return new UiDirection
        {
            Rtl = Prop.Deferred(c => c.Localization().Strings.Bind(s => s.Culture.TextInfo.IsRightToLeft)),
            Child = content,
        };
    }
}

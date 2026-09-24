using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Assistant;
using GitBench.Features.Diff;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Search;
using GitBench.Features.Settings;
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
                new DragOverlay(),
                new TabDropIndicator(),
                new SearchEverywhereOverlay(),
                new DialogSurface(),
                new DiffWindowsView(),
                new ReviewWindowsView(),
                new SettingsWindowHost(),
                new RepoIconWindowHost(),
            ],
        }
        .WithController<AppKeybindController>(ctx)
        .WithController<PairingKeybindController>(ctx);

        // Establish the UI writing direction for the whole tree from the active locale, so RTL
        // locales (Arabic) mirror Row/Column and swap the BorderLayout sidebar to the right.
        return Direction.Wrap(content);
    }
}

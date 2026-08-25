using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Assistant;
using GitBench.Features.Diff;
using GitBench.Features.Markdown;
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
        // Dev-only markdown preview (DIFFDINO_MARKDOWN_PREVIEW=1): the whole window becomes the
        // renderer's fixture surface for /verify runs. Nothing else about the app changes — with
        // the variable unset this branch is dead and the normal composition below is untouched.
        if (MarkdownPreviewWidget.IsEnabled)
            return Direction.Wrap(new MarkdownPreviewWidget());

        var content = new Stack
        {
            Children =
            [
                new AppContentWidget(),
                new AssistantOverlay(),
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

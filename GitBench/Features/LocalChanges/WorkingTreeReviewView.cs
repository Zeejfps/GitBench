using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The Changes tab's Review layout: the same two-column review surface a branch review uses — the file
/// tree in a resizable sidebar, every file's diff stacked in one scroll beside it — driven by the
/// working tree instead of a commit range. A file's header checkbox stages it.
///
/// Both columns build against this widget's <c>Provide</c> scope, so they resolve the working-tree
/// review's own <see cref="CommitDetailsViewModel"/> rather than the History pane's, and the file tree
/// picks up the staged-state tracker as its per-file marks.
/// </summary>
internal sealed record WorkingTreeReviewView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var model = ctx.Require<WorkingTreeReviewViewModel>();
        var input = ctx.Require<InputSystem>();

        var body = new Show
        {
            When = model.HasFiles,
            Then = () => Split(ctx, model),
            Else = () => Centered(L.T(s => s.ReviewNoLocalChanges)),
        };

        // The review loop's keys (j/k, Space, v, ?) live on the surface, not the window: the History
        // tab and the list layout must keep their own keys. It never steals focus, so the file tree
        // keeps its arrow keys.
        var main = new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children = [body],
        }.WithController(input, () => new ReviewKeyController(model));

        var content = new Stack
        {
            Children =
            [
                main,
                new Switch<bool>
                {
                    Value = model.CheatsheetOpen,
                    Case = open => open
                        ? new ReviewCheatsheetOverlay
                        {
                            OnClose = model.CloseCheatsheet,
                            MarkKind = ReviewMarkKind.Staged,
                        }
                        : Empty.Widget,
                },
            ],
        };

        return new Provide<IReviewedFileTracker>
        {
            Value = model.ReviewedFiles,
            Child = new Provide<IReviewSurfaceModel>
            {
                Value = model,
                Child = new Provide<CommitDetailsViewModel> { Value = model.Details, Child = content },
            },
        };
    }

    private static IWidget Split(Context ctx, WorkingTreeReviewViewModel model) => new Box
    {
        Background = Theme.Color(s => s.CommitDetailsView.Background),
        Children =
        [
            new BorderLayout
            {
                West = new ResizableSidebar
                {
                    Content = new CommitChangesPanel
                    {
                        SelectedPath = model.ActiveFile,
                        SelectedPaths = model.SelectedPaths,
                        CursorPath = model.SelectionCursor,
                        OnActivate = model.ActivateFile,
                        OnSelect = model.SelectFile,
                        OnSelectAll = model.SelectAllFiles,
                        OnFileContextMenu = (file, point) =>
                            RepoBarContextMenu.Show(ctx, point, model.BuildFileContextMenuItems(file.Path)),
                        OnFolderContextMenu = (paths, point) =>
                            RepoBarContextMenu.Show(ctx, point, model.BuildFolderContextMenuItems(paths)),
                    },
                    InitialWidth = 300f,
                    MinResizeWidth = 220f,
                    MaxResizeWidth = 560f,
                },
                Center = new ReviewDiffPanel(),
            },
        ],
    };

    private static IWidget Centered(Prop<string?> text) => new Center
    {
        Child = new Text
        {
            Value = text,
            FontSize = FontSize.Body,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.Palette.TextSecondary),
        },
    };
}

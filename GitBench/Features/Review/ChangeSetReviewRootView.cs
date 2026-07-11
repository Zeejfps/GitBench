using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// Root widget hosted inside a cross-repo review window: the <see cref="ChangeSetReviewHeaderBar"/>
/// across the top and, below it, the union of every member's combined change list as a PR-style
/// two-column split — the reused <see cref="CommitChangesPanel"/> file tree (grouped by repo, each
/// member a top-level folder) on the left, the <see cref="ReviewDiffPanel"/> stacked surface on the
/// right — both driven by the window's own <see cref="CommitDetailsViewModel"/>. Mirrors
/// <see cref="ReviewWindowRootView"/>; the only difference is the surface model it provides
/// (<see cref="ChangeSetReviewViewModel"/>) and the header bar.
/// </summary>
internal sealed record ChangeSetReviewRootView : Widget
{
    public required ChangeSetReviewViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var body = new Switch<ChangeSetReviewContentKind>
        {
            Value = Model.ContentKind,
            Case = kind => kind switch
            {
                ChangeSetReviewContentKind.Loading => new FadeIn { Bloom = true, Child = Centered(L.T(s => s.ReviewLoading)) },
                ChangeSetReviewContentKind.Message => new FadeIn { Bloom = true, Child = Message() },
                _ => new FadeIn { Child = Split(ctx) },
            },
        };

        var input = ctx.Require<InputSystem>();
        var main = new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children =
            [
                new BorderLayout
                {
                    North = new ChangeSetReviewHeaderBar(),
                    Center = body,
                },
            ],
        }.WithController(input, () => new ReviewKeyController(Model));

        var content = new Stack
        {
            Children =
            [
                main,
                new Switch<bool>
                {
                    Value = Model.CheatsheetOpen,
                    Case = open => open
                        ? new ReviewCheatsheetOverlay { OnClose = Model.CloseCheatsheet }
                        : Empty.Widget,
                },
            ],
        };

        // The marks tracker and the surface seam are provided across the whole window so the reused
        // Changes list and stacked diff list resolve them and paint per-file Viewed marks. The header
        // bar sits beneath the concrete window model.
        return new Provide<IReviewedFileTracker>
        {
            Value = Model.ReviewedFiles,
            Child = new Provide<IReviewSurfaceModel>
            {
                Value = Model,
                Child = new Provide<ChangeSetReviewViewModel> { Value = Model, Child = content },
            },
        };
    }

    private IWidget Split(Context ctx) => new Provide<CommitDetailsViewModel>
    {
        Value = Model.Details,
        Child = new Box
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
                            SelectedPath = Model.ActiveFile,
                            SelectedPaths = Model.SelectedPaths,
                            CursorPath = Model.SelectionCursor,
                            OnActivate = Model.ActivateFile,
                            OnSelect = Model.SelectFile,
                            OnSelectAll = Model.SelectAllFiles,
                            OnFileContextMenu = (file, point) =>
                                RepoBarContextMenu.Show(ctx, point, Model.BuildFileContextMenuItems(file.Path)),
                            OnFolderContextMenu = (paths, point) =>
                                RepoBarContextMenu.Show(ctx, point, Model.BuildFolderContextMenuItems(paths)),
                        },
                        InitialWidth = 340f,
                        MinResizeWidth = 240f,
                        MaxResizeWidth = 600f,
                    },
                    Center = new ReviewDiffPanel(),
                },
            ],
        },
    };

    private IWidget Message() => Centered(Prop.Bind(Model.PlaceholderText));

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

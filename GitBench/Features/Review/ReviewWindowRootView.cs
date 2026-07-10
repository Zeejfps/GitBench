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
/// Root widget hosted inside a review window: the <see cref="ReviewHeaderBar"/> across the top and,
/// below it, the range's combined change list as a PR-style two-column split — the reused
/// <see cref="CommitChangesPanel"/> file tree in a resizable sidebar on the left, the
/// <see cref="ReviewDiffPanel"/> stacked diff surface on the right — both driven by the window's
/// own <see cref="CommitDetailsViewModel"/>. While the range loads the body shows a loading state;
/// an empty range / load error shows a centered message. Bound to the
/// <see cref="ReviewWindowViewModel"/> supplied by the opening <see cref="ReviewWindowsView"/>, which
/// it also provides into the subtree.
/// </summary>
internal sealed record ReviewWindowRootView : Widget
{
    public required ReviewWindowViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var body = new Switch<ReviewContentKind>
        {
            Value = Model.ContentKind,
            Case = kind => kind switch
            {
                ReviewContentKind.Loading => new FadeIn { Bloom = true, Child = Centered(L.T(s => s.ReviewLoading)) },
                ReviewContentKind.Message => new FadeIn { Bloom = true, Child = Message() },
                _ => new FadeIn { Child = Split(ctx) },
            },
        };

        // Window-level keyboard for the review loop (j/k files, Space primary action, v viewed,
        // ? cheatsheet). Attached to the main box so it sits in the hover/bubble path for the whole
        // window; it never steals focus, so the file list keeps its own arrow-key focus.
        var input = ctx.Require<InputSystem>();
        var main = new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children =
            [
                new BorderLayout
                {
                    North = new ReviewHeaderBar(),
                    Center = body,
                },
            ],
        }.WithController(input, () => new ReviewKeyController(Model));

        // The cheatsheet overlay layers over everything when open; when closed it collapses to a
        // zero-sized child so it never intercepts input meant for the surface below.
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

        // The Viewed tracker is provided across the whole window so the reused Changes list, tab
        // strip, and diff-pane headers resolve it and show their per-file Viewed marks. The stacked
        // list and the key controller bind the surface seam, which the header bar's base chip and
        // window chrome sit beneath as the concrete window model.
        return new Provide<IReviewedFileTracker>
        {
            Value = Model.ReviewedFiles,
            Child = new Provide<IReviewSurfaceModel>
            {
                Value = Model,
                Child = new Provide<ReviewWindowViewModel> { Value = Model, Child = content },
            },
        };
    }

    // The combined change list, PR-style: file tree in a resizable sidebar on the left, every
    // file's diff stacked in one scrolling surface on the right. Both columns build against the
    // Provide scope so they resolve the window's own CommitDetailsViewModel rather than the main
    // window's. Tree activation scrolls the stacked list (instead of opening tabs), and the tree's
    // highlight follows the file being read via the view model's active file.
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
                        // Optimistic base switch: the old range's tree gives way to a skeleton the
                        // instant the reviewer picks a new base, until the new files land.
                        Content = new Switch<bool>
                        {
                            Value = Model.IsSwitchingBase,
                            Case = switching => switching
                                ? new FadeIn { Bloom = true, Child = new ReviewTreeSkeleton() }
                                : new CommitChangesPanel
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
                        },
                        InitialWidth = 340f,
                        MinResizeWidth = 240f,
                        MaxResizeWidth = 600f,
                    },
                    // The stacked diff surface, or a loading indicator while a base switch resolves.
                    Center = new Switch<bool>
                    {
                        Value = Model.IsSwitchingBase,
                        Case = switching => switching
                            ? new FadeIn { Bloom = true, Child = Centered(L.T(s => s.CommonLoading)) }
                            : new ReviewDiffPanel(),
                    },
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

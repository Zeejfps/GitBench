using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The Changes tab's cross-repo Review layout (Phase 5.3): the same two-column review surface the
/// single-repo working-tree review uses — the file tree grouped by repo, every member's diff stacked in
/// one scroll — driven by <see cref="ChangeSetWorkingTreeReviewViewModel"/> over N members' working
/// trees. A file's checkbox stages it in its own repo; the commit bar batch-commits one message across
/// the set (5.4). Both columns build against this widget's <c>Provide</c> scope so they resolve the
/// cross-repo surface's own <see cref="CommitDetailsViewModel"/> and staged-state tracker.
/// </summary>
internal sealed record ChangeSetWorkingTreeReviewView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var model = ctx.Require<ChangeSetWorkingTreeReviewViewModel>();
        var input = ctx.Require<InputSystem>();

        var body = new Show
        {
            When = model.HasFiles,
            Then = () => Split(ctx, model),
            Else = () => TopRuled(Centered(L.T(s => s.ReviewNoLocalChanges))),
        };

        var main = new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children = [body],
        }.WithController(input, () => new ReviewKeyController(model));

        var stacked = new Stack
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

        var withFooter = new BorderLayout
        {
            Center = stacked,
            South = CommitBar(ctx, model),
        };

        return new Provide<IReviewedFileTracker>
        {
            Value = model.ReviewedFiles,
            Child = new Provide<IReviewSurfaceModel>
            {
                Value = model,
                Child = new Provide<CommitDetailsViewModel> { Value = model.Details, Child = withFooter },
            },
        };
    }

    private static IWidget Split(Context ctx, ChangeSetWorkingTreeReviewViewModel model) => new Box
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
                        HeaderActions =
                        [
                            new LocalChangesHeaderActionButton
                            {
                                Icon = Direction.Glyph(ctx, LucideIcons.ChevronRight, LucideIcons.ChevronLeft),
                                Command = model.StageSelected,
                                Tooltip = L.T(s => s.LocalchangesStageSelectedTooltip),
                            },
                            new LocalChangesHeaderActionButton
                            {
                                Icon = Direction.Glyph(ctx, LucideIcons.ChevronsRight, LucideIcons.ChevronsLeft),
                                Command = model.StageAll,
                                Tooltip = L.T(s => s.LocalchangesStageAllTooltip),
                            },
                            new LocalChangesHeaderActionButton
                            {
                                Icon = Direction.Glyph(ctx, LucideIcons.ChevronsLeft, LucideIcons.ChevronsRight),
                                Command = model.UnstageAll,
                                Tooltip = L.T(s => s.LocalchangesUnstageAllTooltip),
                            },
                            new LocalChangesHeaderActionButton
                            {
                                Icon = Direction.Glyph(ctx, LucideIcons.ChevronLeft, LucideIcons.ChevronRight),
                                Command = model.UnstageSelected,
                                Tooltip = L.T(s => s.LocalchangesUnstageSelectedTooltip),
                            },
                        ],
                    },
                    InitialWidth = 300f,
                    MinResizeWidth = 220f,
                    MaxResizeWidth = 560f,
                },
                Center = TopRuled(new ReviewDiffPanel()),
            },
        ],
    };

    // A compact commit bar for the whole set: one title, one Commit button. Committing opens the
    // per-repo staged-summary confirm dialog (5.4) — the git work is fire-and-forget through
    // ChangeSetOperations.CommitInAll, so no busy/amend chrome is needed here.
    private static IWidget CommitBar(Context ctx, ChangeSetWorkingTreeReviewViewModel model)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var loc = ctx.Localization();

        var titleInput = new TextInputView(ctx.Canvas) { TextWrap = TextWrap.NoWrap };
        titleInput.Bind(loc.Strings, s => titleInput.PlaceholderText = s.LocalchangesCommitTitlePlaceholder);
        titleInput.BindThemed(theme, s =>
        {
            titleInput.BackgroundColor = s.TextInput.Background;
            titleInput.TextColor = s.TextInput.Text;
            titleInput.CaretColor = s.TextInput.Caret;
            titleInput.SelectionRectColor = s.TextInput.Selection;
            titleInput.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        var titleController = new TextInputViewKbmController(titleInput, input, ctx.Get<IClipboard>());
        titleInput.UseController(input, titleController);
        titleInput.BindTwoWay(model.CommitTitle, model.SetCommitTitle);

        var commitWidget = new ActionDialogButton
        {
            Label = L.T(s => s.ChangesetsCommitButton),
            Role = DialogButtonRole.Primary,
            Command = model.Commit,
            MinWidth = 140f,
            Height = Sizes.ControlHeight,
        };
        var commitButton = commitWidget.BuildView(ctx);
        commitButton.UseController(input, new KbmController(commitWidget.State));

        return new FooterPanel
        {
            Children =
            [
                TitleBox(titleInput),
                new Row
                {
                    Gap = Spacing.Sm,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new Grow { Child = Empty.Widget },
                        new Raw { View = commitButton },
                    ],
                },
            ],
        };
    }

    private static IWidget TitleBox(TextInputView titleInput) => new Box
    {
        Background = Theme.Color(s => s.TextInput.Background),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Xs, Bottom = Spacing.Xs },
                Children = [new Raw { View = titleInput }],
            },
        ],
    };

    private static IWidget TopRuled(IWidget child) => new Box
    {
        BorderSize = new BorderSizeStyle { Top = 1 },
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.FileChangesSection.HeaderBorder }),
        Children = [child],
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

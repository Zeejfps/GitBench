using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

/// <summary>
/// Confirmation modal for `git worktree remove`. Refuses if the worktree has uncommitted
/// changes or untracked files unless Force is checked (delegates the safety check to git).
/// </summary>
internal sealed record RemoveWorktreeDialog : Widget
{
    // Mirrors the frame width Build() applies, so the path pre-wrap math below stays in sync.
    private const float DialogWidth = DialogFrame.WidthStandard;
    private const float CodeBlockInnerPadding = 8f;

    public required Repo Primary { get; init; }
    public required Repo Worktree { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new RemoveWorktreeDialogViewModel(
            new RemoveWorktreeRequest(Primary, Worktree),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;

        // Path strings have no whitespace, so the framework's word-wrap engine can't break
        // them. Pre-wrap by inserting newlines at path-separator boundaries so the displayed
        // block stays inside the dialog's content width.
        var pathTextStyle = new TextStyle
        {
            FontFamily = DiffOptions.MonoFontFamily,
            FontSize = FontSize.Body,
            TextWrap = TextWrap.Wrap,
        };
        var available = DialogWidth
                        - 2 * DialogFrame.DefaultPadding
                        - 2 * CodeBlockInnerPadding
                        - 2 // account for the 1px border on each side of the code-block
                        - DialogFrame.CloseButtonSize - Spacing.Sm; // the copy button's column
        var wrappedPath = PathWrap.Wrap(Worktree.Path, pathTextStyle, available, ctx.Canvas);

        var pathBox = new DialogInsetCard
        {
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = (int)CodeBlockInnerPadding,
                        Right = (int)CodeBlockInnerPadding,
                        Top = Spacing.Sm,
                        Bottom = Spacing.Sm,
                    },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Start,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = wrappedPath,
                                        FontFamily = DiffOptions.MonoFontFamily,
                                        FontSize = FontSize.Body,
                                        Wrap = TextWrap.Wrap,
                                        Color = Theme.Color(t => t.DialogBody.BodyText),
                                    },
                                },
                                new DialogCopyButton { GetText = () => Worktree.Path },
                            ],
                        },
                    ],
                },
            ],
        };

        return new Dialog
        {
            Title = s.WorktreesRemoveTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.WorktreesRemoveAction, DialogButtonRole.Destructive),
            Command = vm.Remove,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.WorktreesRemoveConfirm(Worktree.DisplayName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                pathBox,
                new CheckboxWidget
                {
                    Label = s.WorktreesRemoveForceLabel,
                    Checked = vm.Force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
                new Text
                {
                    Value = s.WorktreesRemoveNote,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}

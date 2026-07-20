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
    private const int CodeBlockInnerPadding = 8;

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

        var pathBox = new DialogInsetCard
        {
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = CodeBlockInnerPadding,
                        Right = CodeBlockInnerPadding,
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
                                        Value = Worktree.Path,
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

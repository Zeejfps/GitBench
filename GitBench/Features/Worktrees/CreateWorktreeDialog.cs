using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

/// <summary>
/// Modal shown from a primary RepoRow's "New worktree…" menu. Collects the three
/// fields `git worktree add` needs (path, start point, optional new branch name) plus
/// a force toggle for re-using an existing dirty path.
/// </summary>
internal sealed record CreateWorktreeDialog : Widget
{
    public required Repo Primary { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CreateWorktreeDialogViewModel(
            new CreateWorktreeRequest(Primary),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        var browseButton = new SecondaryDialogButton
        {
            Label = s.CommonBrowse,
            Command = new Command(() => PickPath(ctx, vm)),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>();

        return new Dialog
        {
            Title = s.WorktreesCreateTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonCreate, DialogButtonRole.Primary),
            Command = vm.Create,
            Body =
            [
                new LabeledInput
                {
                    Label = s.WorktreesCreatePathLabel,
                    Value = vm.Path,
                    Accessory = browseButton,
                },
                new LabeledInput
                {
                    Label = s.WorktreesCreateStartPointLabel,
                    Value = vm.StartPoint,
                    Hint = s.WorktreesCreateStartPointHint,
                },
                new LabeledInput
                {
                    Label = s.WorktreesCreateNewBranchLabel,
                    Value = vm.NewBranchName,
                    Hint = s.WorktreesCreateNewBranchHint,
                    Status = vm.NewBranchStatus,
                },
                new CheckboxWidget
                {
                    Label = s.WorktreesCreateForceLabel,
                    Checked = vm.Force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static void PickPath(Context ctx, CreateWorktreeDialogViewModel vm)
    {
        var picker = ctx.Get<IFilePicker>();
        picker?.PickFolder(ctx.Localization().Strings.Value.WorktreesCreatePickerTitle,
            picked => vm.Path.Value = picked);
    }
}

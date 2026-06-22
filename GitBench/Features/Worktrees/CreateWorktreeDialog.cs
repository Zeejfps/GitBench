using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
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
            ctx.Require<IMessageBus>());

        var browseButton = new SecondaryDialogButton
        {
            Label = "Browse…",
            Command = new Command(() => PickPath(ctx, vm)),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>();

        return new Dialog
        {
            Title = "New worktree",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Create", DialogButtonRole.Primary),
            Command = vm.Create,
            Body =
            [
                new LabeledInput
                {
                    Label = "Worktree path",
                    Value = vm.Path,
                    Accessory = browseButton,
                },
                new LabeledInput
                {
                    Label = "Start point",
                    Value = vm.StartPoint,
                    Hint = "Branch, tag, or commit SHA.",
                },
                new LabeledInput
                {
                    Label = "New branch (optional)",
                    Value = vm.NewBranchName,
                    Hint = "Leave blank to check out the start point as-is.",
                    Status = vm.NewBranchStatus,
                },
                new CheckboxWidget
                {
                    Label = "Force (allow non-empty path or re-used branch)",
                    Checked = vm.Force,
                    Height = 22,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static void PickPath(Context ctx, CreateWorktreeDialogViewModel vm)
    {
        var shell = ctx.Get<IPlatformShell>();
        var picked = shell?.PickFolder("Select worktree location");
        if (!string.IsNullOrEmpty(picked))
        {
            vm.Path.Value = picked;
        }
    }
}

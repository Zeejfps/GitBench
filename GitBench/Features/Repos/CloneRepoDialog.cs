using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Modal shown from the "Add Repository" menu's Clone entry. Collects a remote URL, a parent
/// directory (with a Browse button), and the subfolder name, then runs <c>git clone</c> and
/// opens the result. See <see cref="CloneRepoDialogViewModel"/>.
/// </summary>
internal sealed record CloneRepoDialog : Widget
{
    public required Action OnClose { get; init; }

    public Guid? TargetGroupId { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CloneRepoDialogViewModel(
            ctx.Require<IGitService>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            TargetGroupId);

        var s = ctx.Localization().Strings.Value;

        // No fixed Width — the button sizes to its label (it carries its own 16px horizontal
        // padding), so pinning a width clips "Browse…". Height matches the field beside it and the
        // footer buttons so the dialog's chrome is one size.
        var browseButton = new SecondaryDialogButton
        {
            Label = s.CommonBrowse,
            Command = new Command(() =>
                ctx.Get<IPlatformShell>()?.PickFolder(s.ReposPickerChooseClone, picked =>
                    vm.ParentDir.Value = picked)),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>();

        return new Dialog
        {
            Title = s.ReposCloneTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.ReposCloneAction, DialogButtonRole.Primary),
            Command = vm.Clone,
            Body =
            [
                new LabeledInput
                {
                    Label = s.CommonRepositoryUrl,
                    Value = vm.Url,
                    Placeholder = s.ReposCloneUrlPlaceholder,
                },
                new LabeledInput
                {
                    Label = s.ReposCloneParentDirLabel,
                    Value = vm.ParentDir,
                    Hint = s.ReposCloneParentDirHint,
                    Accessory = browseButton,
                },
                new LabeledInput
                {
                    Label = s.ReposCloneFolderNameLabel,
                    Value = vm.FolderName,
                },
            ],
        };
    }
}

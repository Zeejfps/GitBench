using GitBench.Controls.Dialogs;
using GitBench.Features.Identity;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Modal shown from the "Add Repository" menu's Clone entry. Collects a remote URL, a parent
/// directory (with a Browse button), the subfolder name, and the identity profile to clone under,
/// then runs <c>git clone</c> and opens the result. See <see cref="CloneRepoDialogViewModel"/>.
/// </summary>
internal sealed record CloneRepoDialog : Widget
{
    public required Action OnClose { get; init; }

    public Guid? TargetGroupId { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CloneRepoDialogViewModel(
            ctx.Require<IGitRemoteOperations>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IdentityProfileService>(),
            ctx.Require<GitIdentityService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization(),
            TargetGroupId);

        var s = ctx.Localization().Strings.Value;

        // No fixed Width — the button sizes to its label (it carries its own 16px horizontal
        // padding), so pinning a width clips "Browse…". Height matches the field beside it and the
        // footer buttons so the dialog's chrome is one size.
        var browseButton = new SecondaryDialogButton
        {
            Label = s.CommonBrowse,
            Command = new Command(() =>
                ctx.Get<IFilePicker>()?.PickFolder(s.ReposPickerChooseClone, picked =>
                    vm.ParentDir.Value = picked)),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>();

        List<IWidget> body =
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
        ];

        // The row only exists when there's something to pick: with no profiles configured the
        // dialog is exactly what it was before.
        if (vm.Profiles.Count > 0)
        {
            body.Add(new LabeledRow
            {
                Label = s.ReposCloneIdentityLabel,
                Value = new IdentityProfileDropdown
                {
                    Selected = vm.ProfileId,
                    Effective = vm.EffectiveProfile,
                    AutoMatched = vm.ProfileIsAutoMatched,
                    Profiles = vm.Profiles,
                },
            });
        }

        return new Dialog
        {
            Title = s.ReposCloneTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.ReposCloneAction, DialogButtonRole.Primary),
            Command = vm.Clone,
            Body = [.. body],
        };
    }
}

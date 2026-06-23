using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

// Add/edit form for a single identity profile: a display name, the author name/email injected
// per commit, an optional SSH key, and a single host/owner match rule. Power users can add
// multiple match rules by editing identity-profiles.json directly; the form keeps to one.
internal sealed record IdentityProfileEditDialog : Widget
{
    public required IdentityProfile? Existing { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new IdentityProfileEditDialogViewModel(
            Existing,
            ctx.Require<IdentityProfileService>(),
            ctx.Require<IUiDispatcher>());

        var add = Existing == null;
        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = add ? s.IdentityTitleNew : s.IdentityTitleEdit,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = (add ? s.CommonAdd : s.CommonSave, DialogButtonRole.Primary),
            Command = vm.Save,
            Body =
            [
                new LabeledInput
                {
                    Label = s.IdentityProfileNameLabel,
                    Value = vm.DisplayName,
                    Placeholder = s.IdentityProfileNamePlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentityAuthorNameLabel,
                    Value = vm.AuthorName,
                    Placeholder = s.IdentityAuthorNamePlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentityAuthorEmailLabel,
                    Value = vm.AuthorEmail,
                    Placeholder = s.IdentityAuthorEmailPlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentitySshKeyLabel,
                    Value = vm.SshKeyPath,
                    Placeholder = s.IdentitySshKeyPlaceholder,
                    Hint = s.IdentitySshKeyHint,
                },
                new LabeledInput
                {
                    Label = s.IdentityMatchHostLabel,
                    Value = vm.MatchHost,
                    Placeholder = s.IdentityMatchHostPlaceholder,
                    Hint = s.IdentityMatchHostHint,
                },
                new LabeledInput
                {
                    Label = s.IdentityMatchOwnerLabel,
                    Value = vm.MatchOwner,
                    Placeholder = s.IdentityMatchOwnerPlaceholder,
                    Hint = s.IdentityMatchOwnerHint,
                },
            ],
        };
    }
}

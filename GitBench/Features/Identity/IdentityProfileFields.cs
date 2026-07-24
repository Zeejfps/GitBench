using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

/// <summary>
/// The identity-profile field set — display name, author name/email, an optional SSH key with a
/// Browse… picker, and a single host/owner match rule — bound to caller-owned state. Shared by the
/// add/edit dialog and the profile manager so both edit identical fields with one layout. Its
/// <see cref="LabeledInput"/>s register with the surrounding dialog's submit/focus wiring.
/// </summary>
internal sealed record IdentityProfileFields : Widget
{
    public required State<string> DisplayName { get; init; }
    public required State<string> AuthorName { get; init; }
    public required State<string> AuthorEmail { get; init; }
    public required State<string> SshKeyPath { get; init; }
    public required State<string> MatchHost { get; init; }
    public required State<string> MatchOwner { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;

        var browseSshKey = new SecondaryDialogButton
        {
            Label = s.CommonBrowse,
            Command = new Command(() =>
                ctx.Get<IFilePicker>()?.PickFile(
                    s.IdentityPickerChooseSshKey,
                    IdentityProfileEditing.InitialSshKeyDirectory(SshKeyPath.Value),
                    filters: null,
                    picked => SshKeyPath.Value = picked)),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>();

        return new Column
        {
            Gap = 12f,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new LabeledInput
                {
                    Label = s.IdentityProfileNameLabel,
                    Value = DisplayName,
                    Placeholder = s.IdentityProfileNamePlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentityAuthorNameLabel,
                    Value = AuthorName,
                    Placeholder = s.IdentityAuthorNamePlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentityAuthorEmailLabel,
                    Value = AuthorEmail,
                    Placeholder = s.IdentityAuthorEmailPlaceholder,
                },
                new LabeledInput
                {
                    Label = s.IdentitySshKeyLabel,
                    Value = SshKeyPath,
                    Placeholder = s.IdentitySshKeyPlaceholder,
                    Hint = s.IdentitySshKeyHint,
                    Accessory = browseSshKey,
                },
                new LabeledInput
                {
                    Label = s.IdentityMatchHostLabel,
                    Value = MatchHost,
                    Placeholder = s.IdentityMatchHostPlaceholder,
                    Hint = s.IdentityMatchHostHint,
                },
                new LabeledInput
                {
                    Label = s.IdentityMatchOwnerLabel,
                    Value = MatchOwner,
                    Placeholder = s.IdentityMatchOwnerPlaceholder,
                    Hint = s.IdentityMatchOwnerHint,
                },
            ],
        };
    }
}

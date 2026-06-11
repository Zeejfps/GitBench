using GitBench.Controls.Dialogs;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
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

    protected override View CreateView(Context ctx)
    {
        var vm = new IdentityProfileEditDialogViewModel(
            Existing,
            ctx.Require<IdentityProfileService>(),
            ctx.Require<IUiDispatcher>());

        var add = Existing == null;
        var view = new Dialog
        {
            Title = add ? "New identity" : "Edit identity",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = (add ? "Add" : "Save", DialogButtonRole.Primary),
            Command = vm.Save,
            Body =
            [
                new LabeledInput
                {
                    Label = "Profile name",
                    Value = vm.DisplayName,
                    Placeholder = "Work",
                },
                new LabeledInput
                {
                    Label = "Author name",
                    Value = vm.AuthorName,
                    Placeholder = "Jane Dev",
                },
                new LabeledInput
                {
                    Label = "Author email",
                    Value = vm.AuthorEmail,
                    Placeholder = "jane@company.com",
                },
                new LabeledInput
                {
                    Label = "SSH key (optional)",
                    Value = vm.SshKeyPath,
                    Placeholder = "~/.ssh/id_work",
                    Hint = "Used for fetch/push from repos matched by this profile.",
                },
                new LabeledInput
                {
                    Label = "Match: host",
                    Value = vm.MatchHost,
                    Placeholder = "github.com",
                    Hint = "Repos whose remote is on this host use this profile.",
                },
                new LabeledInput
                {
                    Label = "Match: owner (optional)",
                    Value = vm.MatchOwner,
                    Placeholder = "your-org",
                    Hint = "Limit to one org/user. Leave blank to match any repo on the host.",
                },
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
    }
}

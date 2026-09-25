using GitBench.Controls;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

// One selectable row in the profile manager's left list: the profile's display name over its author
// email, selected while it is the active edit target.
internal sealed record IdentityProfileListRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var profile = ctx.Require<IdentityProfile>();
        var vm = ctx.Require<IdentityProfileManagerDialogViewModel>();
        return new SelectableListRow
        {
            Title = profile.DisplayName,
            Caption = profile.UserEmail,
            IsSelected = () => vm.SelectedId.Value == profile.Id,
            OnSelect = () => vm.Select(profile.Id),
            Delete = new Command(() => vm.RequestDelete(profile.Id)),
            DeleteTooltip = L.T(s => s.IdentityManageDelete),
        };
    }
}

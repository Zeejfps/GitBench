using System.Diagnostics.CodeAnalysis;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// Names each row variant's controller target so a call site reads `row.WithController<RepoRowController>()`
// — the controller is explicit, the target interface is implied by the variant. Keyed to the concrete
// variant (not IWidget<IRepoRow>) because both variants share RepoRowState — which is an IRepoRow — so an
// interface-typed receiver couldn't tell them apart.
internal static class RowControllerExtensions
{
    public static IWidget WithController<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TController>(this PrimaryRepoRow row)
        where TController : class, IKeyboardMouseController =>
        row.WithController<TController, IRepoRow>();

    public static IWidget WithController<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TController>(this NavigableRepoRow row)
        where TController : class, IKeyboardMouseController =>
        row.WithController<TController, INavigableRow>();

    public static IWidget WithController<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TController>(this RepoRailTile tile)
        where TController : class, IKeyboardMouseController =>
        tile.WithController<TController, INavigableRow>();
}

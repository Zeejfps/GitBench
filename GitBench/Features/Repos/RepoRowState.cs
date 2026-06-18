using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// The interaction surface a row controller drives: the hover flag the visuals bind to, the activate
// command, and the row's context-menu items. NavigableRowController binds to this.
public interface INavigableRow
{
    State<bool> Hovered { get; }
    ICommand Activate { get; }
    IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems();
}

// Adds the live repo record the primary row needs to register and start a drag-to-reorder.
public interface IRepoRow : INavigableRow
{
    Repo Repo { get; }
}

// Live state for a RepoRow: owns the hover flag and forwards the row's command and menu to its
// view model.
internal sealed class RepoRowState(RepoNodeViewModel vm) : IRepoRow
{
    public State<bool> Hovered { get; } = new(false);
    public Repo Repo => vm.Repo;
    public ICommand Activate => vm.Activate;
    public IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems() => vm.BuildMenuItems();
}

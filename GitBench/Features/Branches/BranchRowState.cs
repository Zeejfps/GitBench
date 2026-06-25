using GitBench.Features.Repos;
using ZGF.Observable;

namespace GitBench.Features.Branches;

// The interaction surface a branch row controller drives: the hover + context-highlight flags the
// row visual binds to, and the click / activate / context-menu dispatch for the row's kind.
internal interface IBranchRowInteraction
{
    State<bool> Hovered { get; }
    State<bool> ContextHighlighted { get; }
    void Click();
    void Activate();
    IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems();
}

// Per-row interaction state: owns the hover/context flags and routes the row's click (select or
// toggle), double-click (checkout / apply), and context menu to the shared BranchesViewModel based
// on the row's variant.
internal sealed class BranchRowState(BranchRow row, BranchesViewModel vm) : IBranchRowInteraction, IDisposable
{
    public State<bool> Hovered { get; } = new(false);
    public State<bool> ContextHighlighted { get; } = new(false);

    public void Click()
    {
        switch (row)
        {
            case LocalHeaderRow: vm.ToggleLocalSection(); break;
            case RemotesHeaderRow: vm.ToggleRemotesSection(); break;
            case StashesHeaderRow: vm.ToggleStashesSection(); break;
            case RemoteHeaderRow r: vm.ToggleRemote(r.RemoteName); break;
            case FolderRow f: vm.ToggleFolder(f.Folder); break;
            case LocalBranchRow b: vm.SelectLocalBranch(b.Name, b.TipSha); break;
            case RemoteBranchRow b: vm.SelectRemoteBranch(b.RemoteName, b.Name, b.TipSha); break;
            case StashRow s: vm.SelectStash(s.Label, s.TipSha); break;
        }
    }

    public void Activate()
    {
        switch (row)
        {
            case LocalBranchRow b: vm.ActivateLocalBranch(b.Name, b.IsHead); break;
            case RemoteBranchRow b: vm.ActivateRemoteBranch(b.RemoteName, b.Name); break;
            case StashRow s: vm.ActivateStash(s.Index, s.Label, s.DisplayName); break;
        }
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems() => row switch
    {
        LocalHeaderRow => vm.BuildLocalFolderMenu(new BranchFolder(BranchScope.Local, string.Empty)),
        RemotesHeaderRow => vm.BuildRemotesHeaderMenuItems(),
        StashesHeaderRow => vm.BuildStashesHeaderMenuItems(),
        RemoteHeaderRow r => vm.BuildRemoteHeaderMenuItems(r.RemoteName),
        FolderRow { Folder.Scope.IsRemote: true } f => vm.BuildRemoteFolderMenu(f.Folder),
        FolderRow f => vm.BuildLocalFolderMenu(f.Folder),
        LocalBranchRow b => vm.BuildLocalBranchMenuItems(b.Name, b.IsHead),
        RemoteBranchRow b => vm.BuildRemoteBranchMenuItems(b.RemoteName, b.Name),
        StashRow s => vm.BuildStashMenuItems(s.Index, s.Label, s.DisplayName),
        _ => Array.Empty<RepoBarContextMenu.Item>(),
    };

    public void Dispose()
    {
        Hovered.Dispose();
        ContextHighlighted.Dispose();
    }
}

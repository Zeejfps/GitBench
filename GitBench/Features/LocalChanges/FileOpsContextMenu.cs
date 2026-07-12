using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Localization;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The file-operation context-menu items shared by every working-tree file list — the Changes tab's
/// list panels and its Review layout's file tree — so right-clicking a file offers the same
/// operations everywhere. Each item acts on the subset of the targets its operation applies to
/// (stage on paths with unstaged edits, unstage on paths with staged content) and hides itself when
/// that subset is empty, which is what lets one builder serve both the side-scoped panels and the
/// merged review tree.
/// </summary>
internal sealed class FileOpsContextMenu
{
    private readonly LocalChangesViewModel _vm;
    private readonly ILocalizationService _loc;

    public FileOpsContextMenu(LocalChangesViewModel vm, ILocalizationService loc)
    {
        _vm = vm;
        _loc = loc;
    }

    /// <summary>Mark-resolved / stage / unstage / discard / stash over the targets. Shortcut hints
    /// belong to the caller — only the list panels have side-scoped Enter/Delete gestures.</summary>
    public void AppendFileOps(
        List<RepoBarContextMenu.Item> items,
        IReadOnlyList<string> targets,
        string? stageShortcut = null,
        string? unstageShortcut = null,
        string? discardShortcut = null)
    {
        if (targets.Count == 0) return;
        var s = _loc.Strings.Value;

        var toStage = Present(targets, _vm.Unstaged.Value);
        var toUnstage = Present(targets, _vm.Staged.Value);

        var conflicted = _vm.ConflictedAmong(targets);
        if (conflicted.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.FilesMarkResolved(conflicted.Count),
                () => _vm.MarkResolved(conflicted),
                LucideIcons.CheckSquare));

        if (toStage.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.FilesStage(toStage.Count),
                () => _vm.Stage(toStage),
                LucideIcons.ChevronRight,
                Shortcut: stageShortcut));
        if (toUnstage.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.FilesUnstage(toUnstage.Count),
                () => _vm.Unstage(toUnstage),
                LucideIcons.ChevronLeft,
                Shortcut: unstageShortcut));
        if (toStage.Count > 0)
            items.Add(new RepoBarContextMenu.Item(
                s.FilesDiscard(toStage.Count),
                () => _vm.RequestDiscard(toStage),
                LucideIcons.Trash,
                Shortcut: discardShortcut));
        items.Add(new RepoBarContextMenu.Item(
            s.FilesStash(targets.Count),
            () => _vm.StashSelected(targets),
            LucideIcons.Stash));
    }

    /// <summary>Copy path / full path / file name over the targets, then open-containing-folder and
    /// open-in-terminal on the representative (the clicked folder itself, otherwise the file).</summary>
    public void AppendUtilities(
        List<RepoBarContextMenu.Item> items,
        IReadOnlyList<string> targets,
        string representativePath)
    {
        var s = _loc.Strings.Value;
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyPathMenu, () => _vm.CopyPaths(targets), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyFullPathMenu, () => _vm.CopyAbsolutePaths(targets), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyFileNameMenu, () => _vm.CopyFileNames(targets), LucideIcons.Copy));
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesOpenContainingFolderMenu, () => _vm.OpenContainingFolder(representativePath), LucideIcons.FolderOpen));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesOpenInTerminalMenu, () => _vm.OpenInTerminal(representativePath), LucideIcons.SquareTerminal));
    }

    // The subset of targets present in the given side's list, in target order.
    private static List<string> Present(IReadOnlyList<string> targets, IReadOnlyList<FileChange> side)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in side) set.Add(f.Path);
        var result = new List<string>(targets.Count);
        foreach (var p in targets)
            if (set.Contains(p)) result.Add(p);
        return result;
    }
}

using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitBench.Features.Repos;

internal static class AddRepoMenu
{
    public static IReadOnlyList<RepoBarContextMenu.Item> Items(Context ctx, Guid? groupId = null)
    {
        var s = ctx.Localization().Strings.Value;
        return
        [
            new(s.ReposMenuOpenFromFolder, () => OpenFromFolder(ctx, groupId), Icon: LucideIcons.FolderOpen),
            new(s.ReposMenuCloneRepository, () => ShowCloneDialog(ctx, groupId), Icon: LucideIcons.FolderGit2),
            new(s.ReposMenuNewRepository, () => InitNewRepo(ctx, groupId), Icon: LucideIcons.FolderPlus),
        ];
    }

    public static void OpenFromFolder(Context ctx, Guid? groupId = null)
    {
        var s = ctx.Localization().Strings.Value;
        ctx.Get<IFilePicker>()?.PickFolder(s.ReposPickerOpenRepository, path =>
        {
            if (ctx.Get<IRepoRegistry>()?.Open(path, groupId) == OpenRepoOutcome.NotAGitRepo)
            {
                ctx.Get<IMessageBus>()?.Broadcast(new ShowOperationErrorMessage(
                    s.ReposErrorNotAGitRepoTitle,
                    s.ReposErrorNotAGitRepoMessage(path)));
            }
        });
    }

    // Picks a folder and runs `git init` in it, then opens the result the same way the folder
    // picker above does. An already-initialized folder just opens — git's re-init is a no-op.
    public static void InitNewRepo(Context ctx, Guid? groupId = null)
    {
        var s = ctx.Localization().Strings.Value;
        ctx.Get<IFilePicker>()?.PickFolder(s.ReposPickerNewRepository, path =>
        {
            if (ctx.Get<IGitRepositoryLifecycle>()?.Init(path) is GitOutcome.Failed failed)
            {
                ctx.Get<IMessageBus>()?.Broadcast(new ShowOperationErrorMessage(
                    s.ReposErrorInitFailedTitle, failed.Message));
                return;
            }

            ctx.Get<IRepoRegistry>()?.Open(path, groupId);
        });
    }

    public static void ShowCloneDialog(Context ctx, Guid? groupId = null)
        => ctx.Get<IMessageBus>()?.Broadcast(
            new ShowDialogMessage(onClose => new CloneRepoDialog { OnClose = onClose, TargetGroupId = groupId }));
}

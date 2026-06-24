using GitBench.Controls;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;

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
        ];
    }

    private static void OpenFromFolder(Context ctx, Guid? groupId)
    {
        var s = ctx.Localization().Strings.Value;
        var path = ctx.Get<IPlatformShell>()?.PickFolder(s.ReposPickerOpenRepository);
        if (string.IsNullOrEmpty(path)) return;
        if (ctx.Get<IRepoRegistry>()?.Open(path, groupId) == OpenRepoOutcome.NotAGitRepo)
        {
            ctx.Get<IMessageBus>()?.Broadcast(new ShowOperationErrorMessage(
                s.ReposErrorNotAGitRepoTitle,
                s.ReposErrorNotAGitRepoMessage(path)));
        }
    }

    private static void ShowCloneDialog(Context ctx, Guid? groupId)
        => ctx.Get<IMessageBus>()?.Broadcast(
            new ShowDialogMessage(onClose => new CloneRepoDialog { OnClose = onClose, TargetGroupId = groupId }));
}

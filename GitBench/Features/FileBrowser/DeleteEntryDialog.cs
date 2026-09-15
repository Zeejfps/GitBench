using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// Confirmation modal shown when the reader picks "Delete" on a row in the browser's tree.
/// </summary>
/// <remarks>
/// Two different questions behind one gesture, and which one is asked is
/// <see cref="IPlatformShell.CanMoveToTrash"/>'s answer rather than a preference: where the OS has a
/// trash the entry moves there and the prompt says so, and where it has none — or where the trash is
/// out of reach — the entry is unlinked and the prompt says that instead, pointing at git as the
/// only thing that brings a tracked file back. Nothing here promises a recovery the platform under
/// it cannot make. A directory takes what is under it either way, which is the only thing "delete
/// this folder" can mean.
/// </remarks>
internal sealed record DeleteEntryDialog : Widget
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required FileBrowserViewModel Browser { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var bus = ctx.Require<IMessageBus>();
        var shell = ctx.Require<IPlatformShell>();
        var browser = Browser;
        var path = Path;
        var isDirectory = IsDirectory;
        var onClose = OnClose;
        var toTrash = shell.CanMoveToTrash;

        var delete = new AsyncCommand(
            ctx.Require<IUiDispatcher>(),
            work: () => Remove(path, isDirectory, toTrash ? shell : null),
            onSuccess: () =>
            {
                browser.Deleted(path);
                bus.Broadcast(new WorkingTreeChangedMessage(browser.RepoId));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(Toast(s, isDirectory, toTrash))));
                onClose();
            });

        return new Dialog
        {
            Title = Title(s, isDirectory, toTrash),
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Action = (toTrash ? s.FileBrowserTrashAction : s.CommonDelete, DialogButtonRole.Destructive),
            Command = delete,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = Body(s, Name, isDirectory, toTrash) },
            ],
        };
    }

    private static string Title(Strings s, bool isDirectory, bool toTrash) => (isDirectory, toTrash) switch
    {
        (true, true) => s.FileBrowserTrashFolderTitle,
        (true, false) => s.FileBrowserDeleteFolderTitle,
        (false, true) => s.FileBrowserTrashFileTitle,
        (false, false) => s.FileBrowserDeleteFileTitle,
    };

    private static string Body(Strings s, string name, bool isDirectory, bool toTrash) => (isDirectory, toTrash) switch
    {
        (true, true) => s.FileBrowserTrashFolderBody(name),
        (true, false) => s.FileBrowserDeleteFolderBody(name),
        (false, true) => s.FileBrowserTrashFileBody(name),
        (false, false) => s.FileBrowserDeleteFileBody(name),
    };

    private static string Toast(Strings s, bool isDirectory, bool toTrash) => (isDirectory, toTrash) switch
    {
        (true, true) => s.ToastFolderTrashed,
        (true, false) => s.ToastFolderDeleted,
        (false, true) => s.ToastFileTrashed,
        (false, false) => s.ToastFileDeleted,
    };

    /// <summary>
    /// Removes the entry, and answers with what went wrong. A failed trash is reported rather than
    /// quietly falling back to unlinking: the reader agreed to a move they can undo, and a delete
    /// they cannot is not the lesser half of it.
    /// </summary>
    private static string? Remove(string path, bool isDirectory, IPlatformShell? trash)
    {
        try
        {
            if (trash is not null) trash.MoveToTrash(path);
            else if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

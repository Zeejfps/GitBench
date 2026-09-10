using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// Confirmation modal shown when the reader picks "Delete" on a row in the browser's tree.
/// </summary>
/// <remarks>
/// The file goes, rather than moving to the trash: neither platform's trash is reachable from here
/// without a shell round-trip, and one that silently failed would be worse than one that was never
/// offered. So the prompt says plainly that it does not come back, and — for anything git is
/// tracking — points at the one place it does. A directory takes what is under it, which is the only
/// way "delete this folder" can mean anything.
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
        var browser = Browser;
        var path = Path;
        var isDirectory = IsDirectory;
        var onClose = OnClose;

        var delete = new AsyncCommand(
            ctx.Require<IUiDispatcher>(),
            work: () => Remove(path, isDirectory),
            onSuccess: () =>
            {
                browser.Deleted(path);
                bus.Broadcast(new WorkingTreeChangedMessage(browser.RepoId));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(
                    isDirectory ? s.ToastFolderDeleted : s.ToastFileDeleted)));
                onClose();
            });

        return new Dialog
        {
            Title = isDirectory ? s.FileBrowserDeleteFolderTitle : s.FileBrowserDeleteFileTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            Command = delete,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = isDirectory ? s.FileBrowserDeleteFolderBody(Name) : s.FileBrowserDeleteFileBody(Name),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }

    private static string? Remove(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

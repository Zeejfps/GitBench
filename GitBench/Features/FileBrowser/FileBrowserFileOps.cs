using GitBench.Messages;
using ZGF.Gui;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The browser's writes to the working tree: making a file or a directory, and removing one. Every
/// one of them is a dialog — a name to type, or a deletion to agree to — so this is only the part
/// that decides what the dialog is asked about, and the rail, the row menu and the Delete key all
/// go through it rather than each opening their own.
/// </summary>
internal sealed class FileBrowserFileOps
{
    private readonly IMessageBus _bus;

    public FileBrowserFileOps(Context ctx) => _bus = ctx.Require<IMessageBus>();

    /// <summary>Asks for a name, and creates it inside <paramref name="directory"/>.</summary>
    public void New(FileBrowserViewModel browser, string directory, NewEntryKind kind) =>
        _bus.Broadcast(new ShowDialogMessage(onClose => new NewEntryDialog
        {
            Parent = directory,
            Kind = kind,
            Browser = browser,
            OnClose = onClose,
        }));

    /// <summary>Asks about deleting a row, and deletes it. A declaration inside a file is not
    /// something on disk, so it is not something this offers to remove.</summary>
    public void Delete(FileBrowserViewModel browser, FileBrowserRow? row)
    {
        if (!CanDelete(row)) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteEntryDialog
        {
            Path = row!.FullPath,
            Name = row.Name,
            IsDirectory = row is FileBrowserRow.Directory,
            Browser = browser,
            OnClose = onClose,
        }));
    }

    public static bool CanDelete(FileBrowserRow? row) =>
        row is FileBrowserRow.Directory or FileBrowserRow.File;
}

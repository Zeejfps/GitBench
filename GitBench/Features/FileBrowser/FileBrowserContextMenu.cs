using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The right-click menu on a browser row: the ways out of the app, and the two ways to carry a path
/// somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// Opening in the OS's default application is here, as an item someone chooses, and is deliberately
/// not what a double-click does. On Windows <see cref="IPlatformShell.OpenFile"/> is
/// <c>UseShellExecute</c>, and unlike every existing caller the path here comes off the filesystem
/// rather than out of git — so a <c>.bat</c>, <c>.command</c> or <c>.lnk</c> dropped by a build or
/// carried by a hostile repository is in reach of the gesture people use to look at a file. The
/// codebase already defends this at the caller rather than in the shell; see
/// <see cref="TerminalLinkTarget"/> for the same reasoning about a link in terminal output.
/// </para>
/// <para>
/// Every shell call is given an absolute, fully-resolved path — a leading <c>-</c> in a filename
/// otherwise parses as an option to <c>open</c> — and every one of them is wrapped, because
/// <c>OpenFile</c>, <c>OpenFolder</c>, <c>OpenTerminal</c> and <c>RevealFile</c> do not catch on
/// macOS or Windows, and a file that vanished between being listed and being clicked would throw out
/// of input dispatch.
/// </para>
/// </remarks>
internal sealed class FileBrowserContextMenu
{
    private readonly ILocalizationService _loc;
    private readonly IPlatformShell? _shell;
    private readonly IClipboard? _clipboard;
    private readonly IMessageBus? _bus;
    private readonly ITerminalSessionStore? _terminals;
    private readonly State<MainViewMode>? _mode;

    public FileBrowserContextMenu(Context ctx)
    {
        _loc = ctx.Localization();
        _shell = ctx.Get<IPlatformShell>();
        _clipboard = ctx.Get<IClipboard>();
        _bus = ctx.Get<IMessageBus>();
        _terminals = ctx.Get<ITerminalSessionStore>();
        _mode = ctx.Get<State<MainViewMode>>();
    }

    public IReadOnlyList<RepoBarContextMenu.Item> Build(FileBrowserViewModel browser, FileBrowserRow? row)
    {
        if (row is null) return [];

        var s = _loc.Strings.Value;
        var full = FullPath(row.FullPath);
        if (full is null) return [];

        var directory = row is FileBrowserRow.Directory ? full : Path.GetDirectoryName(full) ?? full;
        var relative = Path.GetRelativePath(browser.RootPath, full).Replace('\\', '/');

        return
        [
            new RepoBarContextMenu.Item(
                s.FileBrowserOpen,
                () => browser.Activate(row),
                row is FileBrowserRow.Directory ? LucideIcons.FolderOpen : LucideIcons.FileText),
            new RepoBarContextMenu.Item(
                s.FileBrowserOpenInDefaultApp,
                () => Shell(shell =>
                {
                    if (row is FileBrowserRow.Directory) shell.OpenFolder(full);
                    else shell.OpenFile(full);
                }),
                LucideIcons.ExternalLink),
            new RepoBarContextMenu.Item(
                s.FileBrowserReveal,
                () => Shell(shell => shell.RevealFile(full)),
                LucideIcons.FolderOpen),
            new RepoBarContextMenu.Item(
                s.LocalchangesOpenInTerminalMenu,
                () => OpenTerminalAt(directory),
                LucideIcons.SquareTerminal),
            RepoBarContextMenu.Separator,
            new RepoBarContextMenu.Item(
                s.FileBrowserCopyPath,
                () => Copy(full, s.ToastCopiedFullPath),
                LucideIcons.Copy),
            new RepoBarContextMenu.Item(
                s.FileBrowserCopyRelativePath,
                () => Copy(relative, s.ToastCopiedPath),
                LucideIcons.Copy),
        ];
    }

    /// <summary>
    /// A shell in that directory. The pane's own terminal when one is running — switch to it and
    /// type the change of directory, which is the only cwd surface the terminal stack has: a
    /// session's working directory is fixed at spawn and restarting it would throw away the
    /// scrollback. Otherwise the OS's terminal, which is what "open in terminal" already means
    /// everywhere else in the app.
    /// </summary>
    private void OpenTerminalAt(string directory)
    {
        var instance = _terminals?.Tabs.Value?.Active.Value;
        if (instance is { IsAcceptingInput: true }
            && ShellPathQuoting.ChangeDirectoryCommand(directory, ShellCommand.Family) is { } command)
        {
            instance.SendInput(System.Text.Encoding.UTF8.GetBytes(command + "\r"));
            if (_mode is not null) _mode.Value = MainViewMode.Terminal;
            return;
        }

        Shell(shell => shell.OpenTerminal(directory));
    }

    private void Copy(string text, string toast)
    {
        if (_clipboard is null) return;
        _clipboard.SetText(text);
        _bus?.Broadcast(new ShowToastMessage(ToastIntent.Success(toast)));
    }

    private void Shell(Action<IPlatformShell> act)
    {
        if (_shell is null) return;
        try
        {
            act(_shell);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileBrowser] Handing a path to the OS failed: {ex.Message}");
        }
    }

    private static string? FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

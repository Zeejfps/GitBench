using GitBench.Controls.Dialogs;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

internal enum NewEntryKind { File, Folder }

/// <summary>
/// Modal shown when the reader picks "New file…" or "New folder…" on a directory in the browser's
/// tree. One field: what it is called inside that directory.
/// </summary>
/// <remarks>
/// A name with slashes in it creates the folders above it as well, which is the one thing that saves
/// this from being three dialogs to reach <c>src/features/auth/Login.cs</c>. Nothing is overwritten:
/// a name that is already taken is refused while it is being typed, and the create call itself is
/// still exclusive, so a file that appears between the two loses nothing.
/// </remarks>
internal sealed record NewEntryDialog : Widget
{
    /// <summary>The directory it is created in — the folder that was right-clicked.</summary>
    public required string Parent { get; init; }

    public required NewEntryKind Kind { get; init; }

    /// <summary>The tree that shows it afterwards.</summary>
    public required FileBrowserViewModel Browser { get; init; }

    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var loc = ctx.Require<ILocalizationService>();
        var bus = ctx.Require<IMessageBus>();
        var browser = Browser;
        var directory = Parent;
        var kind = Kind;
        var onClose = OnClose;

        var name = new State<string>(string.Empty);
        var status = new Derived<FieldStatus?>(() => NewEntryRules.Validate(name.Value, directory, loc.Strings.Value));
        var gate = new Derived<bool>(() => NewEntryRules.IsAcceptable(name.Value, directory));

        string? created = null;
        var create = new AsyncCommand(
            ctx.Require<IUiDispatcher>(),
            work: () =>
            {
                var path = NewEntryRules.Resolve(name.Value, directory);
                if (path is null) return s.FileBrowserNameExists;
                created = path;
                return Create(path, kind);
            },
            onSuccess: () =>
            {
                if (created is { } path)
                {
                    browser.Created(path, kind == NewEntryKind.Folder);
                    bus.Broadcast(new WorkingTreeChangedMessage(browser.RepoId));
                }
                onClose();
            },
            gate: gate);

        return new Dialog
        {
            Title = kind == NewEntryKind.Folder ? s.FileBrowserNewFolderTitle : s.FileBrowserNewFileTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonCreate, DialogButtonRole.Primary),
            Command = create,
            Body =
            [
                new LabeledInput
                {
                    Label = s.FileBrowserNewEntryNameLabel,
                    Hint = s.FileBrowserNewEntryHint(Label(browser, directory)),
                    Value = name,
                    Status = status,
                },
            ],
        };
    }

    /// <summary>What the hint calls the directory: repo-relative, and the working tree's own folder
    /// name at the root, where there is no relative path to show.</summary>
    private static string Label(FileBrowserViewModel browser, string directory) =>
        PathKey.Comparer.Equals(directory, browser.RootPath)
            ? Path.GetFileName(browser.RootPath)
            : browser.PathLabel(directory);

    /// <summary>
    /// Makes the entry, and answers with what went wrong. The folders above it come first, and the
    /// file itself is opened exclusively — <see cref="FileMode.CreateNew"/> — so the check the
    /// dialog did while the name was typed cannot become an overwrite by the time it is pressed.
    /// </summary>
    private static string? Create(string path, NewEntryKind kind)
    {
        try
        {
            if (kind == NewEntryKind.Folder)
            {
                Directory.CreateDirectory(path);
                return null;
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
                Directory.CreateDirectory(parent);
            using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

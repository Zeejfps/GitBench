using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Editor;

/// <summary>What is about to throw the edits away, which selects the wording.</summary>
internal enum UnsavedChangesKind
{
    Close,
    Quit,

    /// <summary>A git operation about to rewrite the files underneath them.</summary>
    Overwrite,

    /// <summary>The files already moved on disk, and reloading is what discards the edits.</summary>
    ChangedOnDisk,
}

/// <summary>Asks once, for a whole set of files, before edits that are not on disk are lost.</summary>
internal sealed record UnsavedChangesDialog : Widget
{
    public required IReadOnlyList<string> Files { get; init; }

    public required Action OnClose { get; init; }

    /// <summary>Runs when the user agrees to lose them.</summary>
    public required Action OnConfirm { get; init; }

    public UnsavedChangesKind Kind { get; init; } = UnsavedChangesKind.Close;

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var (title, body, action, cancel) = Kind switch
        {
            UnsavedChangesKind.Quit =>
                (s.FileBrowserUnsavedQuitTitle, s.FileBrowserUnsavedQuitBody, s.FileBrowserUnsavedQuitAction,
                    s.FileBrowserUnsavedCancel),
            UnsavedChangesKind.Overwrite =>
                (s.FileBrowserUnsavedOverwriteTitle, s.FileBrowserUnsavedOverwriteBody, s.FileBrowserUnsavedOverwriteAction,
                    s.FileBrowserUnsavedCancel),
            UnsavedChangesKind.ChangedOnDisk =>
                (s.FileBrowserChangedOnDiskTitle, s.FileBrowserChangedOnDiskBody, s.FileBrowserChangedOnDiskAction,
                    s.FileBrowserChangedOnDiskCancel),
            _ =>
                (s.FileBrowserUnsavedCloseTitle, s.FileBrowserUnsavedCloseBody, s.FileBrowserUnsavedCloseAction,
                    s.FileBrowserUnsavedCancel),
        };

        return new Dialog
        {
            Title = title,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            CancelLabel = cancel,
            Action = (action, DialogButtonRole.Destructive, () =>
            {
                OnClose();
                OnConfirm();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = body,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = string.Join(", ", Files),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowText),
                },
            ],
        };
    }
}

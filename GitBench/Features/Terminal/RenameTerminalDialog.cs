using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// Modal shown when the user picks "Rename…" on a terminal tab. One field: what the tab should say.
/// </summary>
/// <remarks>
/// Says nothing about terminals — it is handed the current name and a callback, so the tab keeps
/// deciding what a name means and this stays a dialog. Blank is allowed and is how the name is given
/// back: an empty field reads as "call it whatever is running in it again", which is what the hint
/// says and what <see cref="TerminalInstance.Rename"/> does with it.
/// </remarks>
internal sealed record RenameTerminalDialog : Widget
{
    /// <summary>What the tab says now, which is what the field opens on, selected.</summary>
    public required string CurrentName { get; init; }

    public required Action OnClose { get; init; }

    /// <summary>Runs with what the user typed, once they accept.</summary>
    public required Action<string> OnRename { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var name = new State<string>(CurrentName);
        var onRename = OnRename;
        var onClose = OnClose;

        return new Dialog
        {
            Title = s.TerminalRenameTabTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonRename, DialogButtonRole.Primary, () =>
            {
                onClose();
                onRename(name.Value);
            }),
            Body =
            [
                new LabeledInput
                {
                    Label = s.TerminalRenameTabLabel,
                    Hint = s.TerminalRenameTabHint,
                    Value = name,
                    SelectAllOnOpen = true,
                },
            ],
        };
    }
}

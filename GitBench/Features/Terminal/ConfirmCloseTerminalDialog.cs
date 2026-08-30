using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Terminal;

/// <summary>
/// Confirmation modal shown when a tab holding a live shell is asked to close. A shell mid-build is
/// not something to lose to a stray middle click.
/// </summary>
/// <remarks>
/// Beside <see cref="ConfirmQuitDialog"/> rather than parameterised over it. That one names
/// repositories, because that is what a reader recognises when the whole application is closing;
/// this one is one shell and the reader is looking straight at its tab, so it names that terminal.
/// The sentences are different sentences, and the plural machinery would be carrying a case that
/// never has more than one item.
/// </remarks>
internal sealed record ConfirmCloseTerminalDialog : Widget
{
    /// <summary>What the tab says: the title a program set, or the shell's own name.</summary>
    public required string Terminal { get; init; }

    public required Action OnClose { get; init; }

    /// <summary>Runs when the user agrees. Kept a callback so this stays a dialog rather than
    /// something that knows how a terminal ends.</summary>
    public required Action OnConfirm { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;

        return new Dialog
        {
            Title = s.TerminalCloseTabTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            CancelLabel = s.TerminalCloseTabCancel,
            Action = (s.TerminalCloseTabAction, DialogButtonRole.Destructive, () =>
            {
                OnClose();
                OnConfirm();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.TerminalCloseTabBody,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = Terminal,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowText),
                },
            ],
        };
    }
}

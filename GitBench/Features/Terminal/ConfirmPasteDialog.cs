using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// Confirmation modal shown when a paste of more than one line is headed for a shell that has not
/// asked for bracketed paste. Every line ending in it is a press of Enter, so the paste is not text
/// arriving — it is a list of commands running.
/// </summary>
/// <remarks>
/// <para>
/// Three answers rather than two, because the honest ones are genuinely three: the sender either
/// meant to run all of it, meant to put it on the prompt and look at it first, or reached for the
/// wrong clipboard. A two-button dialog would force the middle case to cancel and re-copy.
/// </para>
/// <para>
/// Shows the first line for the reason <see cref="ConfirmQuitDialog"/> names its repositories:
/// "12 lines" tells the reader to stop without telling them what they would be running, which is the
/// one thing that decides the answer.
/// </para>
/// </remarks>
internal sealed record ConfirmPasteDialog : Widget
{
    public required int Lines { get; init; }

    public required string FirstLine { get; init; }

    public required Action OnClose { get; init; }

    /// <summary>Sends the text as it stands, line endings and all.</summary>
    public required Action OnRun { get; init; }

    /// <summary>Sends it as a single line, which lands on the prompt without running.</summary>
    public required Action OnFlatten { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;

        return new Dialog
        {
            Title = s.TerminalPasteConfirmTitle(Lines),
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            // The safe answer is the primary one: it is the only branch that cannot run anything the
            // sender has not read first.
            Action = (s.TerminalPasteConfirmFlatten, DialogButtonRole.Primary, () =>
            {
                OnClose();
                OnFlatten();
            }),
            ConfirmKeys = true,
            // Left of the cancel, the way a "Don't Save" sits apart from Cancel and Save: it is the
            // answer that acts rather than the one that retreats, and it is the dangerous one.
            FooterLead = new SecondaryDialogButton
            {
                Label = s.TerminalPasteConfirmRun,
                Height = DialogFrame.DefaultButtonHeight,
                Command = new Command(() =>
                {
                    OnClose();
                    OnRun();
                }),
            }.WithController<KbmController>(),
            Body =
            [
                new Text
                {
                    Value = s.TerminalPasteConfirmBody,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.TerminalPasteConfirmFirstLine(FirstLine),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowText),
                },
            ],
        };
    }
}

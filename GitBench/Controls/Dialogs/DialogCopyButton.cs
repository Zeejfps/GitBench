using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Icon-only "copy to clipboard" button styled to match <see cref="DialogCloseButton"/>.
/// On click, copies <see cref="GetText"/>'s current value to the clipboard and momentarily
/// swaps the icon to a checkmark so the user gets feedback without needing to paste to verify.
/// </summary>
public sealed record DialogCopyButton : Widget
{
    private const int FeedbackMs = 1200;

    public required Func<string> GetText { get; init; }
    public Prop<string?> Tooltip { get; init; } = "Copy";

    protected override IWidget Build(Context ctx)
    {
        var clipboard = ctx.Get<IClipboard>();
        var dispatcher = ctx.Get<IUiDispatcher>();
        var icon = new State<string?>(LucideIcons.Copy);
        var feedbackGen = 0;

        void ShowFeedback()
        {
            // Bump generation so a rapid second click doesn't get reverted by the first
            // click's pending Task — only the most recent click controls the final state.
            var gen = ++feedbackGen;
            icon.Value = LucideIcons.CheckSquare;
            Task.Run(async () =>
            {
                await Task.Delay(FeedbackMs);
                dispatcher?.Post(() => { if (gen == feedbackGen) icon.Value = LucideIcons.Copy; });
            });
        }

        return new IconButtonWidget
        {
            Command = new Command(() => { clipboard?.SetText(GetText()); ShowFeedback(); }),
            Icon = icon,
            Width = DialogFrame.CloseButtonSize,
            Height = DialogFrame.CloseButtonSize,
            Surface = s => Theme.Color(t => s.Hovered.Value ? t.DialogIconButton.BackgroundHover : t.DialogIconButton.BackgroundIdle),
            Foreground = s => Theme.Color(t => s.Hovered.Value ? t.DialogIconButton.TextHover : t.DialogIconButton.TextIdle),
        }.WithTooltip(Tooltip).WithController<KbmController>();
    }
}

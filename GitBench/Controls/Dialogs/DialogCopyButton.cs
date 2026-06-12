using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Icon-only "copy to clipboard" button styled to match <see cref="DialogCloseButton"/>.
/// On click, copies <paramref name="getText"/>'s current value to the clipboard and
/// momentarily swaps the icon to a checkmark + the tooltip to "Copied!" so the user gets
/// feedback without needing to paste to verify.
/// </summary>
public sealed class DialogCopyButton : HoverableButton
{
    private const int FeedbackMs = 1200;

    private readonly TextView _label;
    private readonly IClipboard? _clipboard;
    private readonly IUiDispatcher? _dispatcher;
    private readonly Func<string> _getText;
    private int _feedbackGen;

    public DialogCopyButton(Context ctx, Func<string> getText, string tooltip = "Copy")
        : base(ctx, null, tooltip)
    {
        _getText = getText;
        _clipboard = ctx.Get<IClipboard>();
        _dispatcher = ctx.Get<IUiDispatcher>();
        Width = 28;
        Height = 28;

        var theme = ctx.Theme();
        _label = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.Copy,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _label.BindThemedTextColor(theme, s =>
            IsHovered.Value ? s.DialogIconButton.TextHover : s.DialogIconButton.TextIdle);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { _label }
        };
        background.BindThemedBackgroundColor(theme, s =>
            IsHovered.Value ? s.DialogIconButton.BackgroundHover : s.DialogIconButton.BackgroundIdle);

        SetBackground(background);
    }

    protected override void OnClicked()
    {
        _clipboard?.SetText(_getText());
        ShowFeedback();
    }

    private void ShowFeedback()
    {
        // Bump generation so a rapid second click doesn't get reverted by the first
        // click's pending Task — only the most recent click controls the final state.
        var gen = ++_feedbackGen;
        _label.Text = LucideIcons.CheckSquare;
        var dispatcher = _dispatcher;
        Task.Run(async () =>
        {
            await Task.Delay(FeedbackMs);
            if (dispatcher != null)
                dispatcher.Post(() => { if (gen == _feedbackGen) _label.Text = LucideIcons.Copy; });
        });
    }
}

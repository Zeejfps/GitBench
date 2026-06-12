using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

public sealed class DialogCloseButton : HoverableButton
{
    public DialogCloseButton(Context ctx, Action onClick, string? tooltip = "Close")
        : base(ctx, onClick, tooltip)
    {
        Width = 28;
        Height = 28;

        var theme = ctx.Theme();
        var label = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.X,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(theme, s =>
            IsHovered.Value ? s.DialogIconButton.TextHover : s.DialogIconButton.TextIdle);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label }
        };
        background.BindThemedBackgroundColor(theme, s =>
            IsHovered.Value ? s.DialogIconButton.BackgroundHover : s.DialogIconButton.BackgroundIdle);

        SetBackground(background);
    }
}

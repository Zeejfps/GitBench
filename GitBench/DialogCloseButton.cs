using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class DialogCloseButton : HoverableButton
{
    public DialogCloseButton(Action onClick, string? tooltip = "Close")
        : base(onClick, tooltip)
    {
        Width = 28;
        Height = 28;

        var label = new TextView
        {
            Text = LucideIcons.X,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s =>
            IsHovered.Value ? s.DialogIconButton.TextHover : s.DialogIconButton.TextIdle);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label }
        };
        background.BindThemedBackgroundColor(s =>
            IsHovered.Value ? s.DialogIconButton.BackgroundHover : s.DialogIconButton.BackgroundIdle);

        SetBackground(background);
    }
}

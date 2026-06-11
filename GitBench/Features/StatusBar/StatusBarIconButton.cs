using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Small icon-only button sized to fit inside the <see cref="StatusBarView"/>. The icon glyph
/// is driven through <see cref="BindIcon"/> so callers can swap it reactively (e.g. sun/moon
/// for the theme toggle). Chrome mirrors <see cref="DialogCloseButton"/> but uses the
/// status-bar palette.
/// </summary>
internal sealed record StatusBarIconButton : Widget
{
    public string? Tooltip { get; init; }

    /// <summary>Auto-tracked icon glyph.</summary>
    public required Func<string> BindIcon { get; init; }

    /// <summary>Angle (radians) of the glyph; drive it from a <see cref="SpinnerAnimation"/>
    /// to spin a <see cref="LucideIcons.Loader"/> while a background op runs.</summary>
    public IReadable<float>? IconRotation { get; init; }

    public required ICommand Command { get; init; }

    protected override View CreateView(Context ctx)
    {
        var view = new ButtonView(ctx, this);
        view.BindCommand(Command);
        return view;
    }

    private sealed class ButtonView : HoverableButton
    {
        public ButtonView(Context ctx, StatusBarIconButton w)
            : base(tooltip: w.Tooltip)
        {
            var theme = ctx.Theme();
            Width = 22;
            Height = 18;

            var label = new TextView(ctx.Canvas)
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            label.BindText(w.BindIcon);
            if (w.IconRotation != null) label.BindRotation(w.IconRotation);
            label.BindTextColor(() => IsHovered.Value ? theme.Styles.Value.StatusBar.IconHover : theme.Styles.Value.StatusBar.Icon);

            var background = new RectView
            {
                BorderRadius = BorderRadiusStyle.All(4),
                Children = { label },
            };
            background.BindBackgroundColor(() => IsHovered.Value ? theme.Styles.Value.StatusBar.IconHoverBackground : 0u);

            SetBackground(background);
        }
    }
}

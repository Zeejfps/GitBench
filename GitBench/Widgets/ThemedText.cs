using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Text whose color follows the active theme. The GitBench counterpart of the framework's
/// <see cref="ZGF.Gui.Widgets.Text"/>, extended with the props GitBench text actually uses
/// (font family for icon glyphs, wrapping, weight).
/// </summary>
public sealed record ThemedText : Widget
{
    public string? Value { get; init; }

    /// <summary>Selects the text color from the active theme; re-fires on theme swaps.</summary>
    public Func<ThemeStyles, uint>? Color { get; init; }

    public StyleValue<float> FontSize { get; init; }
    public string? FontFamily { get; init; }
    public StyleValue<FontWeight> Weight { get; init; }
    public StyleValue<TextWrap> Wrap { get; init; }
    public StyleValue<TextAlignment> HAlign { get; init; }
    public StyleValue<TextAlignment> VAlign { get; init; }

    /// <summary>Auto-tracked text binding; overrides <see cref="Value"/> once mounted.</summary>
    public Func<string?>? Bind { get; init; }

    protected override View CreateView(Context ctx)
    {
        var v = new TextView(ctx.Canvas) { Text = Value };
        if (FontSize.IsSet) v.FontSize = FontSize;
        if (FontFamily != null) v.FontFamily = FontFamily;
        if (Weight.IsSet) v.FontWeight = Weight;
        if (Wrap.IsSet) v.TextWrap = Wrap;
        if (HAlign.IsSet) v.HorizontalTextAlignment = HAlign;
        if (VAlign.IsSet) v.VerticalTextAlignment = VAlign;
        if (Bind != null) v.BindText(Bind);
        if (Color != null)
        {
            var theme = ctx.Theme();
            v.BindTextColor(() => Color(theme.Styles.Value));
        }
        return v;
    }
}

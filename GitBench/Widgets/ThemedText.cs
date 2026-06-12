using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Text whose color follows the active theme. Composes the framework's
/// <see cref="ZGF.Gui.Widgets.Text"/>, adding only the theme-styles color selector.
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

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var color = Color;
        return new Text
        {
            Value = Value,
            FontSize = FontSize,
            FontFamily = FontFamily,
            Weight = Weight,
            Wrap = Wrap,
            HAlign = HAlign,
            VAlign = VAlign,
            Bind = Bind,
            BindColor = color != null ? () => color(theme.Styles.Value) : null,
        };
    }
}

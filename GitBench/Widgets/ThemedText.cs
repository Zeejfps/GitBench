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
    /// <summary>The text: a constant, an observable, a projection, or a compute (see <see cref="Prop{T}"/>).</summary>
    public Prop<string?> Value { get; init; }

    /// <summary>Selects the text color from the active theme; re-fires on theme swaps.</summary>
    public Func<ThemeStyles, uint>? Color { get; init; }

    public Prop<float> FontSize { get; init; }
    public Prop<string> FontFamily { get; init; }
    public Prop<FontWeight> Weight { get; init; }
    public Prop<TextWrap> Wrap { get; init; }
    public Prop<TextAlignment> HAlign { get; init; }
    public Prop<TextAlignment> VAlign { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        return new Text
        {
            Value = Value,
            FontSize = FontSize,
            FontFamily = FontFamily,
            Weight = Weight,
            Wrap = Wrap,
            HAlign = HAlign,
            VAlign = VAlign,
            Color = Color != null ? theme.Styles.Bind(Color) : default,
        };
    }
}

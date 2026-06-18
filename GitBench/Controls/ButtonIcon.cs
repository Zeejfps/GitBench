using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The icon segment of an <see cref="ButtonWidget"/>: a Lucide glyph whose color tracks the ambient
/// <see cref="Foreground"/>, optionally with a count badge hugging it. The badge is null/0-hidden and
/// sits tight against the glyph; the glyph renders correctly inside any <see cref="Foreground"/> scope.
/// </summary>
internal sealed record ButtonIcon : Widget
{
    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Value { get; init; }

    /// <summary>Glyph size; defaults to the toolbar size. Smaller insets (e.g. a 24px header) pass a smaller value.</summary>
    public Prop<float> FontSize { get; init; } = 15;

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> Rotation { get; init; }

    /// <summary>Count shown in a badge next to the icon; null or 0 hides it.</summary>
    public Prop<int?> Badge { get; init; }

    /// <summary>Badge text color — bind a theme color with <see cref="Theme.Color"/>.</summary>
    public Prop<uint> BadgeColor { get; init; }

    protected override IWidget Build(Context ctx) => new Row
    {
        Gap = 0,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize,
                VAlign = TextAlignment.Center,
                Value = Value,
                Rotation = Rotation,
                Color = Foreground.Color,
            },
            new Text
            {
                VAlign = TextAlignment.Center,
                Color = BadgeColor,
                Value = Badge.Select(count => count?.ToString()),
                Visible = Badge.Select(count => count is > 0),
            },
        ],
    };
}

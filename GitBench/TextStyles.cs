using ZGF.Gui;

namespace GitGui;

/// <summary>
/// Factory helpers for the recurring TextStyle shapes used by the row-rendering views.
/// Most rows render text as "vertically centered, horizontally left-aligned"; the helpers
/// here capture that default and let callers override just the color, alignment, or font
/// without re-spelling the rest of the style on every site.
/// </summary>
internal static class TextStyles
{
    /// <summary>Color + vertical-center + left-aligned. The default row-text shape.</summary>
    public static TextStyle Row(uint color) => new()
    {
        TextColor = color,
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };

    /// <summary>Color + vertically centered + horizontally centered.</summary>
    public static TextStyle Centered(uint color) => new()
    {
        TextColor = color,
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Center,
    };

    /// <summary>Row shape rendered with the Lucide icon font.</summary>
    public static TextStyle Icon(uint color, float size = 14f) => new()
    {
        TextColor = color,
        FontFamily = LucideIcons.FontFamily,
        FontSize = size,
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };
}

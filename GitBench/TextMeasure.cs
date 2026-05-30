using ZGF.Gui;

namespace GitGui;

internal static class TextMeasure
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Returns <paramref name="text"/> shortened with a trailing ellipsis to fit in
    /// <paramref name="available"/> pixels when rendered with <paramref name="style"/>.
    /// Returns the input unchanged when it already fits; returns a bare ellipsis when
    /// even the ellipsis itself is wider than the available width.
    /// </summary>
    public static string TruncateToFit(string text, TextStyle style, float available, ICanvas canvas)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (available <= 0f) return string.Empty;
        if (canvas.MeasureTextWidth(text, style) <= available) return text;

        var ellipsisWidth = canvas.MeasureTextWidth(Ellipsis, style);
        if (ellipsisWidth > available) return Ellipsis;

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (canvas.MeasureTextWidth(text.AsSpan(0, mid), style) + ellipsisWidth <= available)
                lo = mid;
            else
                hi = mid - 1;
        }
        return text[..lo] + Ellipsis;
    }
}

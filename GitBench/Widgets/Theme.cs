using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Theme-sourced <see cref="Prop{T}"/> values. <see cref="Color"/> defers theme resolution to the
/// build <see cref="Context"/>, so a themed color is an ordinary <see cref="Prop{T}"/> that is
/// ctx-free to author and re-binds on theme swaps — no wrapper widget or bespoke
/// <c>Func&lt;ThemeStyles, uint&gt;</c> selector prop required.
/// </summary>
public static class Theme
{
    /// <summary>A text/background/border color selected from the active theme styles.</summary>
    public static Prop<uint> Color(Func<ThemeStyles, uint> select) =>
        Prop.Deferred(ctx => ctx.Theme().Styles.Bind(select));

    /// <summary>A per-edge border color selected from the active theme styles.</summary>
    public static Prop<BorderColorStyle> BorderColor(Func<ThemeStyles, BorderColorStyle> select) =>
        Prop.Deferred(ctx => ctx.Theme().Styles.Bind(select));

    /// <summary>The active theme's scrollbar colors, for a <c>ScrollArea</c>/<c>ScrollBar</c>.</summary>
    public static Prop<ScrollBarStyle> ScrollBar() =>
        Prop.Deferred(ctx => ctx.Theme().Styles.Bind(s => ToScrollBarStyle(s.ScrollBar)));

    private static ScrollBarStyle ToScrollBarStyle(ScrollBarStyles s) => new()
    {
        TrackBackground = s.TrackBackground,
        TrackBorderSize = BorderSizeStyle.All(1),
        TrackBorder = BorderColorStyle.All(s.TrackBorder),
        ThumbIdleBackground = s.ThumbIdleBackground,
        ThumbHoverBackground = s.ThumbHoverBackground,
        ThumbBorderSize = BorderSizeStyle.All(1),
        ThumbBorder = BorderColorStyle.All(s.ThumbBorder),
    };
}

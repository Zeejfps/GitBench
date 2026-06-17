using GitBench.Widgets;
using ZGF.Gui;

namespace GitBench.Theming;

/// <summary>
/// Theme-sourced <see cref="Prop{T}"/> values. <see cref="Color"/> defers theme resolution to the
/// build <see cref="Context"/>, so a themed color is an ordinary <see cref="Prop{T}"/> that is
/// ctx-free to author and re-binds on theme swaps — no <c>ThemedText</c> wrapper or bespoke
/// <c>Func&lt;ThemeStyles, uint&gt;</c> selector prop required.
/// </summary>
public static class Theme
{
    /// <summary>A text/background/border color selected from the active theme styles.</summary>
    public static Prop<uint> Color(Func<ThemeStyles, uint> select) =>
        Prop.Deferred(ctx => ctx.Theme().Styles.Bind(select));
}

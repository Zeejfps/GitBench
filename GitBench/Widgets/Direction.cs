using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// The active locale's writing direction, for composed-widget Build code that must pick a
/// direction-relative glyph (an expand chevron). Read it inside a <c>Prop.Bind</c>/compute so it
/// tracks the locale and re-picks on a language switch. Layout mirroring itself is handled lower down
/// by inherited <see cref="ZGF.Gui.Views.View.IsRtl"/>; this is only for glyph choice in widgets,
/// which have no <c>View</c> at build time.
/// </summary>
internal static class Direction
{
    public static bool IsRtl(Context ctx) => ctx.Localization().Strings.Value.Culture.TextInfo.IsRightToLeft;

    /// <summary>
    /// A live direction-relative glyph: <paramref name="ltr"/> in left-to-right locales,
    /// <paramref name="rtl"/> (its mirrored counterpart) otherwise, re-picked on a language switch.
    /// </summary>
    public static Prop<string?> Glyph(Context ctx, string ltr, string rtl) =>
        Prop.Bind<string?>(() => IsRtl(ctx) ? rtl : ltr);

    /// <summary>
    /// Establishes the locale's writing direction on a tree root. <see cref="ZGF.Gui.Views.View.IsRtl"/>
    /// is inherited, so this belongs at the top of every window's tree — the main window and each
    /// secondary window, whose roots don't hang off the main tree and would otherwise stay LTR.
    /// </summary>
    public static IWidget Wrap(IWidget child) => new UiDirection
    {
        Rtl = Prop.Deferred(c => c.Localization().Strings.Bind(s => s.Culture.TextInfo.IsRightToLeft)),
        Child = child,
    };
}

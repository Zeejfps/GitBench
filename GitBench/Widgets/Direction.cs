using GitBench.Localization;
using ZGF.Gui;

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
}

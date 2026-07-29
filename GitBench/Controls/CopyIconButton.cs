using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Icon-only "copy this to the clipboard" button for content that sits in the page rather than in a
/// dialog — a code block, an assistant reply. Quiet until hovered, so a surface can carry several
/// without becoming a row of buttons.
/// </summary>
/// <remarks>
/// The text is taken at press time, so a value still being written copies as it stands rather than
/// as it was when the button was built. With no <c>IClipboard</c> registered the press is inert
/// rather than a throw.
/// </remarks>
internal sealed record CopyIconButton : Widget
{
    /// <summary>What is being copied, localized: the tooltip, and the accessible name the glyph
    /// itself cannot carry (a PUA codepoint reads as nothing).</summary>
    public required Func<Strings, string> Label { get; init; }

    /// <summary>The text to copy, read when the button is pressed.</summary>
    public required Func<string> GetText { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var clipboard = ctx.Get<IClipboard>();
        var label = Label;
        var text = GetText;

        return new IconButtonWidget
        {
            Command = new Command(() => clipboard?.SetText(text())),
            Icon = LucideIcons.Copy,
            Width = Sizes.RowHeight,
            Height = Sizes.RowHeight,
            Accessibility = new AccessibilityInfo(
                AccessibilityRole.Button, label(ctx.Localization().Strings.Value)),
            Surface = s => Theme.Color(t => s.Hovered.Value ? t.Palette.SurfaceHover : 0u),
            Foreground = s => Theme.Color(t => s.Hovered.Value ? t.Palette.TextPrimary : t.Palette.TextMuted),
        }.WithTooltip(L.T(label)).WithController<KbmController>();
    }
}

using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The visual treatment of an <see cref="ActionButton"/>: <see cref="Plain"/> is the transparent, themed
/// toolbar look; <see cref="Filled"/> is a solid rounded chip with white glyphs (the banner call-to-action).
/// The button composes a style and reads its colors and chrome from it, so the plain-vs-filled distinction
/// lives here as one styling value instead of branching through the button.
/// </summary>
internal sealed record ButtonStyle
{
    private readonly uint? _fill;

    private ButtonStyle(uint? fill) => _fill = fill;

    /// <summary>Transparent surface with the themed idle/hover/disabled ramp.</summary>
    public static readonly ButtonStyle Plain = new(fill: null);

    /// <summary>A solid rounded chip in <paramref name="color"/>, lightening on hover / darkening when disabled.</summary>
    public static ButtonStyle Filled(uint color) => new(color);

    public BorderRadiusStyle Radius => _fill is null ? default : BorderRadiusStyle.All(6);

    /// <summary>Horizontal inset for an icon-only button: tighter for the plain toolbar look, chunkier
    /// for the filled chip. Labeled buttons use <see cref="ActionButton.ContentInset"/>'s default.</summary>
    public PaddingStyle IconOnlyInset
    {
        get
        {
            var pad = _fill is null ? 6 : 10;
            return new PaddingStyle { Left = pad, Right = pad };
        }
    }

    public Prop<uint> Surface(IInteractable state) => _fill is uint color
        ? Theme.Color(s => s.ActionButton.FilledSurface(color, state))
        : Theme.Color(s => s.ActionButton.Surface(state));

    public Prop<uint> Foreground(IInteractable state) => _fill is null
        ? Theme.Color(s => s.ActionButton.Foreground(state))
        : Theme.Color(s => s.ActionButton.FilledForeground(state));
}

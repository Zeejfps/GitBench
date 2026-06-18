using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The visual treatment of an <see cref="ActionButton"/>: <see cref="Plain"/> is the transparent, themed
/// toolbar look; <see cref="Filled"/> is a solid rounded chip with white glyphs (the banner call-to-action);
/// <see cref="Bare"/> drops the surface and padding entirely (a chrome-less glyph) and takes a caller-supplied
/// color ramp. The button composes a style and reads its colors and chrome from it, so the look lives here as
/// one styling value instead of branching through the button.
/// </summary>
internal sealed record ButtonStyle
{
    private readonly uint? _fill;
    private readonly Func<IInteractable, Prop<uint>>? _bareForeground;

    private ButtonStyle(uint? fill, Func<IInteractable, Prop<uint>>? bareForeground = null)
    {
        _fill = fill;
        _bareForeground = bareForeground;
    }

    /// <summary>Transparent surface with the themed idle/hover/disabled ramp.</summary>
    public static readonly ButtonStyle Plain = new(fill: null);

    /// <summary>A solid rounded chip in <paramref name="color"/>, lightening on hover / darkening when disabled.</summary>
    public static ButtonStyle Filled(uint color) => new(color);

    /// <summary>No surface or padding — just the content, tinted by <paramref name="foreground"/> (resolved from
    /// the button's interaction state). For inline icon buttons in a toolbar/header that own no chrome.</summary>
    public static ButtonStyle Bare(Func<IInteractable, Prop<uint>> foreground) => new(fill: null, bareForeground: foreground);

    /// <summary>True for the chip styles (<see cref="Plain"/>/<see cref="Filled"/>) that wrap content in a
    /// surface box; false for <see cref="Bare"/>, which renders content directly.</summary>
    public bool HasSurface => _bareForeground is null;

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

    public Prop<uint> Foreground(IInteractable state) =>
        _bareForeground is { } bare ? bare(state)
        : _fill is null ? Theme.Color(s => s.ActionButton.Foreground(state))
        : Theme.Color(s => s.ActionButton.FilledForeground(state));
}

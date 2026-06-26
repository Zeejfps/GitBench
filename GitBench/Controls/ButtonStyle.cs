using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The visual treatment of an <see cref="ButtonWidget"/>: <see cref="Plain"/> is the transparent, themed
/// toolbar look; <see cref="Filled"/> is a solid rounded chip with white glyphs (the banner call-to-action);
/// <see cref="Bare"/> drops the surface and padding entirely (a chrome-less glyph) and takes a caller-supplied
/// color ramp. The button composes a style and reads its colors and chrome from it, so the look lives here as
/// one styling value instead of branching through the button.
/// </summary>
internal sealed record ButtonStyle
{
    private readonly uint? _fill;
    private readonly Func<ThemeStyles, uint>? _fillSelect;
    private readonly Func<IInteractable, Prop<uint>>? _bareForeground;
    private readonly Func<ThemeStyles, uint>? _outlineSelect;

    private ButtonStyle(
        uint? fill,
        Func<ThemeStyles, uint>? fillSelect = null,
        Func<IInteractable, Prop<uint>>? bareForeground = null,
        Func<ThemeStyles, uint>? outlineSelect = null)
    {
        _fill = fill;
        _fillSelect = fillSelect;
        _bareForeground = bareForeground;
        _outlineSelect = outlineSelect;
    }

    /// <summary>Transparent surface with the themed idle/hover/disabled ramp.</summary>
    public static readonly ButtonStyle Plain = new(fill: null);

    /// <summary>A solid rounded chip in <paramref name="color"/>, lightening on hover / darkening when disabled.</summary>
    public static ButtonStyle Filled(uint color) => new(color);

    /// <summary>A solid rounded chip whose fill is read from the active theme via <paramref name="select"/>,
    /// so it tracks light/dark instead of baking in a constant.</summary>
    public static ButtonStyle Filled(Func<ThemeStyles, uint> select) => new(fill: null, fillSelect: select);

    /// <summary>No surface or padding — just the content, tinted by <paramref name="foreground"/> (resolved from
    /// the button's interaction state). For inline icon buttons in a toolbar/header that own no chrome.</summary>
    public static ButtonStyle Bare(Func<IInteractable, Prop<uint>> foreground) => new(fill: null, bareForeground: foreground);

    /// <summary>A bordered chip with a transparent fill: the border and glyph/label take <paramref name="select"/>'s
    /// accent (dimmed when disabled, subtle surface on hover). The secondary look beside a <see cref="Filled"/>
    /// primary — one solid call-to-action, the rest outlined.</summary>
    public static ButtonStyle Outline(Func<ThemeStyles, uint> select) => new(fill: null, outlineSelect: select);

    /// <summary>True for the chip styles (<see cref="Plain"/>/<see cref="Filled"/>/<see cref="Outline"/>) that wrap
    /// content in a surface box; false for <see cref="Bare"/>, which renders content directly.</summary>
    public bool HasSurface => _bareForeground is null;

    private bool IsFilled => _fill is not null || _fillSelect is not null;

    private bool IsOutline => _outlineSelect is not null;

    public BorderRadiusStyle Radius => IsFilled || IsOutline ? BorderRadiusStyle.All(GitBench.Widgets.Radius.Md) : default;

    public BorderSizeStyle BorderSize => IsOutline ? BorderSizeStyle.All(1) : default;

    /// <summary>Horizontal inset for an icon-only button: tighter for the plain toolbar look, chunkier
    /// for the filled chip. Labeled buttons use <see cref="ButtonWidget.ContentInset"/>'s default.</summary>
    public PaddingStyle IconOnlyInset
    {
        get
        {
            var pad = IsFilled ? 10 : 6;
            return new PaddingStyle { Left = pad, Right = pad };
        }
    }

    public Prop<uint> Surface(IInteractable state) =>
        _fillSelect is { } select ? Theme.Color(s => s.ActionButton.FilledSurface(select(s), state))
        : _fill is uint color ? Theme.Color(s => s.ActionButton.FilledSurface(color, state))
        : Theme.Color(s => s.ActionButton.Surface(state));

    public Prop<uint> Foreground(IInteractable state) =>
        _bareForeground is { } bare ? bare(state)
        : IsFilled ? Theme.Color(s => s.ActionButton.FilledForeground(state))
        : _outlineSelect is { } accent ? Theme.Color(s => state.Enabled.Value ? accent(s) : s.Palette.TextDisabled)
        : Theme.Color(s => s.ActionButton.Foreground(state));

    public Prop<BorderColorStyle> BorderColor(IInteractable state) =>
        _outlineSelect is { } accent
            ? Theme.BorderColor(s => BorderColorStyle.All(state.Enabled.Value ? accent(s) : s.Palette.TextDisabled))
            : Theme.BorderColor(_ => default);
}

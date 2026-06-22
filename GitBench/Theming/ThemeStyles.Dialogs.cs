using GitBench.Widgets;
using ZGF.Gui.Widgets;

namespace GitBench.Theming;

public sealed record DialogFrameStyles(
    uint Background,
    uint Border,
    uint TitleText,
    uint HeaderSeparator,
    uint ErrorText,
    uint WarningText,
    uint InsetBackground,
    uint Shadow);

public sealed record TextInputStyles(
    uint Background,
    uint Border,
    uint Text,
    uint Caret,
    uint Selection,
    uint PlaceholderText);

public sealed record BorderedButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint BorderIdle,
    uint BorderHover,
    uint Text,
    uint TextDisabled);

public sealed record DialogIconButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint TextIdle,
    uint TextHover)
{
    internal uint Surface(IInteractable s) => s.Hovered.Value ? BackgroundHover : BackgroundIdle;
    internal uint Foreground(IInteractable s) => s.Hovered.Value ? TextHover : TextIdle;
}

public sealed record ActionButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint TextIdle,
    uint TextHover,
    uint TextDisabled)
{
    // Plain glyph/label color: the themed idle/hover/disabled ramp.
    internal uint Foreground(IInteractable s)
    {
        if (!s.Enabled.Value) return TextDisabled;
        return s.Hovered.Value ? TextHover : TextIdle;
    }

    // Plain fill: transparent idle, the themed surface on hover.
    internal uint Surface(IInteractable s) =>
        s.Enabled.Value && s.Hovered.Value ? BackgroundHover : BackgroundIdle;

    // Filled chip glyph/label color: white while enabled, the themed disabled text otherwise.
    internal uint FilledForeground(IInteractable s) =>
        s.Enabled.Value ? 0xFFFFFFFFu : TextDisabled;

    // Filled chip fill, from the caller's base color: lightens on hover, darkens when disabled.
    internal uint FilledSurface(uint color, IInteractable s)
    {
        if (!s.Enabled.Value) return Darken(color, 0x40);
        return s.Hovered.Value ? Lighten(color, 0x18) : color;
    }

    private static uint Lighten(uint argb, uint delta)
    {
        var a = (argb >> 24) & 0xFF;
        var r = Math.Min(0xFFu, ((argb >> 16) & 0xFF) + delta);
        var g = Math.Min(0xFFu, ((argb >> 8) & 0xFF) + delta);
        var b = Math.Min(0xFFu, (argb & 0xFF) + delta);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint Darken(uint argb, uint delta)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        r = r > delta ? r - delta : 0;
        g = g > delta ? g - delta : 0;
        b = b > delta ? b - delta : 0;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}

// Filled footer buttons (the "action" button next to Cancel). Primary uses the theme
// accent; Destructive uses the danger red for delete/discard/force/abort/reset actions.
// Cancel keeps the outline BorderedButton chrome, so the filled button always reads as
// the dialog's commit action.
public sealed record DialogActionButtonStyles(
    uint PrimaryFill,
    uint PrimaryFillHover,
    uint PrimaryText,
    uint DestructiveFill,
    uint DestructiveFillHover,
    uint DestructiveText,
    uint DisabledFill,
    uint DisabledText);

public sealed record CheckboxStyles(
    uint TextIdle,
    uint TextHover,
    uint TextDisabled,
    uint BoxBorderIdle,
    uint BoxBorderHover,
    uint BoxBorderDisabled,
    uint BoxFillChecked,
    uint BoxFillCheckedHover,
    uint BoxFillDisabled,
    uint CheckGlyph)
{
    public uint Foreground(ICheckbox cb)
    {
        if (!cb.Enabled.Value) return TextDisabled;
        return cb.Hovered.Value ? TextHover : TextIdle;
    }

    public uint BoxFill(ICheckbox cb)
    {
        if (!cb.Enabled.Value) return cb.Checked.Value ? BoxFillDisabled : 0x00000000u;
        if (!cb.Checked.Value) return 0x00000000u;
        return cb.Hovered.Value ? BoxFillCheckedHover : BoxFillChecked;
    }

    public uint BoxBorder(ICheckbox cb)
    {
        if (!cb.Enabled.Value) return BoxBorderDisabled;
        if (cb.Checked.Value) return cb.Hovered.Value ? BoxFillCheckedHover : BoxFillChecked;
        return cb.Hovered.Value ? BoxBorderHover : BoxBorderIdle;
    }

    public uint GlyphColor(ICheckbox cb) => cb.Enabled.Value ? CheckGlyph : TextDisabled;
}

public sealed record DialogBodyStyles(
    uint BodyText,
    uint SectionHeaderText,
    uint RowText,
    uint RowTextMissing);

public partial record ThemeStyles
{
    private static DialogFrameStyles BuildDialogFrame(ThemePalette p, StatusPalette status) =>
        new(
            Background: p.Surface,
            Border: p.Border,
            TitleText: p.TextEmphasis,
            HeaderSeparator: p.DialogHeaderSeparator,
            ErrorText: status.DialogError,
            WarningText: status.DialogWarning,
            InsetBackground: p.SurfaceSunken,
            Shadow: p.Shadow);

    private static TextInputStyles BuildTextInput(ThemePalette p) =>
        new(
            Background: p.InputSurface,
            Border: p.BorderStrong,
            Text: p.TextEmphasis,
            Caret: p.TextEmphasis,
            Selection: p.Selection,
            PlaceholderText: p.Placeholder);

    private static BorderedButtonStyles BuildBorderedButton(ThemePalette p) =>
        new(
            BackgroundIdle: p.InputSurface,
            BackgroundHover: p.InputSurfaceHover,
            BorderIdle: p.BorderStrong,
            BorderHover: p.Accent,
            Text: p.TextStrong,
            TextDisabled: p.TextDisabled);

    private static DialogActionButtonStyles BuildDialogActionButton(ThemePalette p, StatusPalette status) =>
        new(
            PrimaryFill: p.Accent,
            PrimaryFillHover: p.AccentHover,
            PrimaryText: p.TextOnAccent,
            DestructiveFill: status.Danger,
            DestructiveFillHover: Lighten(status.Danger, 0x18),
            DestructiveText: 0xFFFFFFFFu,
            DisabledFill: p.InputSurface,
            DisabledText: p.TextDisabled);

    private static DialogIconButtonStyles BuildDialogIconButton(ThemePalette p) =>
        new(
            BackgroundIdle: 0u,
            BackgroundHover: p.SurfaceHoverStrong,
            TextIdle: p.TextSubtle,
            TextHover: p.TextStrong);

    private static ActionButtonStyles BuildActionButton(ThemePalette p) =>
        new(
            BackgroundIdle: 0u,
            BackgroundHover: p.SurfaceHoverStrong,
            TextIdle: p.TextSubtle,
            TextHover: p.TextStrong,
            TextDisabled: p.TextDisabled);

    private static CheckboxStyles BuildCheckbox(ThemePalette p) =>
        new(
            TextIdle: p.TextSecondary,
            TextHover: p.TextStrong,
            TextDisabled: p.TextDisabled,
            BoxBorderIdle: p.CheckboxBorderIdle,
            BoxBorderHover: p.BorderMutedHover,
            BoxBorderDisabled: WithAlpha(p.CheckboxBorderIdle, 0x66),
            BoxFillChecked: p.Accent,
            BoxFillCheckedHover: p.AccentHover,
            BoxFillDisabled: p.CheckboxDisabledFill,
            CheckGlyph: p.TextOnAccent);

    private static DialogBodyStyles BuildDialogBody(ThemePalette p) =>
        new(
            BodyText: p.TextBody,
            SectionHeaderText: p.TextMuted,
            RowText: p.TextSecondary,
            RowTextMissing: p.TextDisabled);
}

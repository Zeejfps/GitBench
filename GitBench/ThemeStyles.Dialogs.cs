namespace GitGui;

public sealed record DialogFrameStyles(
    uint Background,
    uint Border,
    uint TitleText,
    uint HeaderSeparator,
    uint ErrorText,
    uint InsetBackground);

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
    uint TextHover);

public sealed record ActionButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint TextIdle,
    uint TextHover,
    uint TextDisabled);

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
    uint CheckGlyph);

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
            InsetBackground: p.SurfaceSunken);

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

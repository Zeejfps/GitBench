namespace GitGui;

public sealed record OperationRowStyles(
    uint IconText,
    uint LabelText,
    uint PhaseTextIdle,
    uint ElapsedText,
    uint BackgroundIdle,
    uint BackgroundHover,
    uint SuccessBar,
    uint SuccessText,
    uint FailureBar,
    uint FailureText);

public sealed record OperationsStatusBarStyles(
    uint ContainerBackground,
    uint ContainerBorder,
    uint LogBackground,
    uint LogBorder,
    uint LogText);

public partial record ThemeStyles
{
    private static OperationRowStyles BuildOperationRow(ThemePalette p, StatusPalette status) =>
        new(
            IconText: p.TextEmphasis,
            LabelText: p.TextEmphasis,
            PhaseTextIdle: p.TextFaint,
            ElapsedText: p.TextFaint,
            BackgroundIdle: p.BarSurface,
            BackgroundHover: p.OperationRowHoverBg,
            SuccessBar: status.SuccessBar,
            SuccessText: status.SuccessText,
            FailureBar: status.DangerBar,
            FailureText: status.DangerText);

    private static OperationsStatusBarStyles BuildOperationsStatusBar(ThemePalette p) =>
        new(
            ContainerBackground: p.BarSurface,
            ContainerBorder: p.Border,
            LogBackground: p.SurfaceSunken,
            LogBorder: p.Border,
            LogText: p.TextSecondary);
}

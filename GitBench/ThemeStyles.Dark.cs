namespace GitGui;

public partial record ThemeStyles
{
    private static ThemeStyles BuildDark()
    {
        var p = new ThemePalette(
            Surface: 0xFF1E1F22u,
            SurfaceRaised: 0xFF222326u,
            SurfaceSunken: 0xFF1A1B1Eu,
            SurfaceMuted: 0xFF2A2C30u,
            SurfaceHover: 0xFF2B2D31u,
            SurfaceHoverStrong: 0xFF3A3D43u,
            SurfaceSelected: 0xFF404C8Cu,
            SurfaceSelectedSubtle: 0xFF404C8Cu,
            Border: 0xFF313338u,
            BorderStrong: 0xFF3E4047u,
            BorderMuted: 0xFF4A4D52u,
            BorderMutedHover: 0xFF6A6D72u,
            BorderHoverFill: 0xFF4A5680u,
            BorderHoverLine: 0xFF7A8DC8u,
            Accent: 0xFF5865F2u,
            AccentHover: 0xFF7480F5u,
            TextStrong: 0xFFFFFFFFu,
            TextPrimary: 0xFFE6E6E6u,
            TextBody: 0xFFDCDDDEu,
            TextSecondary: 0xFFB5B9C0u,
            TextMedium: 0xFFB5B9C0u,
            TextMuted: 0xFF96989Du,
            TextDim: 0xFF7A7C81u,
            TextDisabled: 0x80B5B9C0u,
            TextOnAccent: 0xFFFFFFFFu,
            Shadow: 0x80000000u,
            BarSurface: 0xFF2A2C30u,
            InputSurface: 0xFF2B2D31u,
            InputSurfaceHover: 0xFF3A3D43u,
            TextEmphasis: 0xFFE6E6E6u,
            TextSubtle: 0xFFB5B9C0u,
            TextFaint: 0xFF7A7C81u,
            OnStatusText: 0xFF1A1B1Eu,
            RowSubtleText: 0xFFFFFFFFu,
            HunkOutline: 0xFF5A8DD6u,
            Selection: 0xFF404C8Cu,
            Placeholder: 0x80B5B9C0u,
            DialogHeaderSeparator: 0xFF2A2C30u,
            CheckboxBorderIdle: 0xFF4A4D52u,
            CheckboxDisabledFill: 0xFF2B2D31u,
            SegmentActiveBg: 0xFF404C8Cu,
            ScrollBarTrackBg: 0xFF26272Bu,
            ScrollBarThumbBorder: 0xFF2A2C30u,
            OperationRowHoverBg: 0xFF313338u,
            CommitRowSelectedBg: 0xFF404C8Cu,
            CommitRowSelectedText: 0xFFFFFFFFu);

        var status = new StatusPalette(
            Success: 0xFF57F287u,
            Warning: 0xFFE9C77Au,
            Danger: 0xFFED4245u,
            Info: 0xFF5DADE2u,
            Purple: 0xFFB57EDCu,
            SuccessSoft: 0xFF9DD17Bu,
            WarningSoft: 0xFFE6A85Cu,
            SuccessBar: 0xFF4E8B3Du,
            SuccessText: 0xFF7FB76Au,
            SuccessLineBg: 0xFF284534u,
            SuccessLineGlyph: 0xFF57F287u,
            DangerBar: 0xFFB3514Bu,
            DangerText: 0xFFE9C77Au,
            DangerLineBg: 0xFF432528u,
            DangerLineGlyph: 0xFFED4245u,
            Other: 0xFF9B59B6u,
            DialogError: 0xFFE06C75u,
            DiffError: 0xFFE9C77Au);

        var banner = new BannerStyles(
            Background: 0xFF3D2E14u,
            Border: 0xFFB89050u,
            Text: 0xFFE9C77Au);

        var tooltip = new TooltipPalette(
            Background: p.SurfaceMuted,
            Border: p.Border,
            Text: p.TextPrimary);

        var hunkButton = new DiffHunkButtonPalette(
            BackgroundIdle: 0xCC2C313Au,
            BackgroundHover: 0xFF3B4150u,
            Border: 0xFF4A5060u,
            Text: 0xFFE6E8ECu);

        var commitBadge = new CommitBadgePalette(
            LocalBg: 0xFF2F4A6Bu,
            RemoteBg: 0xFF4A2F6Bu,
            HeadBg: 0xFF6B4A2Fu,
            Text: p.TextPrimary);

        return BuildStyles(p, status, banner, tooltip, hunkButton, commitBadge);
    }
}

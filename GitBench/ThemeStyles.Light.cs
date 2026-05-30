namespace GitGui;

public partial record ThemeStyles
{
    private static ThemeStyles BuildLight()
    {
        var textMutedLight = 0xFF6B7280u;

        var p = new ThemePalette(
            Surface: 0xFFFFFFFFu,
            SurfaceRaised: 0xFFF3F4F6u,
            SurfaceSunken: 0xFFF9FAFBu,
            SurfaceMuted: 0xFFF3F4F6u,
            SurfaceHover: 0xFFF3F4F6u,
            SurfaceHoverStrong: 0xFFE5E7EBu,
            SurfaceSelected: 0xFF4F46E5u,
            SurfaceSelectedSubtle: 0xFFE0E7FFu,
            Border: 0xFFE5E7EBu,
            BorderStrong: 0xFFD1D5DBu,
            BorderMuted: 0xFFC1C5CBu,
            BorderMutedHover: 0xFF9CA3AFu,
            BorderHoverFill: 0xFFCBD5E1u,
            BorderHoverLine: 0xFF94A3B8u,
            Accent: 0xFF4F46E5u,
            AccentHover: 0xFF6366F1u,
            TextStrong: 0xFF111827u,
            TextPrimary: 0xFF111827u,
            TextBody: 0xFF1F2937u,
            TextSecondary: 0xFF374151u,
            TextMedium: 0xFF4B5563u,
            TextMuted: textMutedLight,
            TextDim: 0xFF9CA3AFu,
            TextDisabled: 0x80374151u,
            TextOnAccent: 0xFFFFFFFFu,
            Shadow: 0x40000000u,
            BarSurface: 0xFFF3F4F6u,
            InputSurface: 0xFFFFFFFFu,
            InputSurfaceHover: 0xFFF3F4F6u,
            TextEmphasis: 0xFF111827u,
            TextSubtle: textMutedLight,
            TextFaint: textMutedLight,
            OnStatusText: 0xFFFFFFFFu,
            RowSubtleText: 0xFF1E1B4Bu,
            HunkOutline: 0xFF3B82F6u,
            Selection: 0xFFCBD5E1u,
            Placeholder: WithAlpha(textMutedLight, 0x80),
            DialogHeaderSeparator: 0xFFE5E7EBu,
            CheckboxBorderIdle: 0xFFD1D5DBu,
            CheckboxDisabledFill: 0xFFE5E7EBu,
            SegmentActiveBg: 0xFF4F46E5u,
            ScrollBarTrackBg: 0xFFF3F4F6u,
            ScrollBarThumbBorder: 0xFFE5E7EBu,
            OperationRowHoverBg: 0xFFE5E7EBu,
            CommitRowSelectedBg: 0xFFE0E7FFu,
            CommitRowSelectedText: 0xFF111827u);

        var status = new StatusPalette(
            Success: 0xFF16A34Au,
            Warning: 0xFFCA8A04u,
            Danger: 0xFFDC2626u,
            Info: 0xFF2563EBu,
            Purple: 0xFFA855F7u,
            SuccessSoft: 0xFF16A34Au,
            WarningSoft: 0xFFEA580Cu,
            SuccessBar: 0xFF16A34Au,
            SuccessText: 0xFF166534u,
            SuccessLineBg: 0xFFDCFCE7u,
            SuccessLineGlyph: 0xFF15803Du,
            DangerBar: 0xFFDC2626u,
            DangerText: 0xFF7C2D12u,
            DangerLineBg: 0xFFFEE2E2u,
            DangerLineGlyph: 0xFFB91C1Cu,
            Other: 0xFF7C3AEDu,
            DialogError: 0xFFDC2626u,
            DiffError: 0xFF92400Eu);

        var banner = new BannerStyles(
            Background: 0xFFFEF3C7u,
            Border: 0xFFD97706u,
            Text: 0xFF78350Fu);

        var tooltip = new TooltipPalette(
            Background: p.TextSecondary,
            Border: p.TextBody,
            Text: p.TextOnAccent);

        var hunkButton = new DiffHunkButtonPalette(
            BackgroundIdle: WithAlpha(p.BorderStrong, 0xCC),
            BackgroundHover: p.SurfaceHoverStrong,
            Border: p.BorderStrong,
            Text: p.TextStrong);

        var commitBadge = new CommitBadgePalette(
            LocalBg: 0xFFDBEAFEu,
            RemoteBg: 0xFFEDE9FEu,
            HeadBg: 0xFFFEF3C7u,
            Text: p.TextBody);

        return BuildStyles(p, status, banner, tooltip, hunkButton, commitBadge);
    }
}

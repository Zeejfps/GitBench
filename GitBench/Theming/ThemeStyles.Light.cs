namespace GitBench.Theming;

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
            BorderSubtle: 0xFFE5E7EBu,
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
            TextMuted: textMutedLight,
            TextDim: 0xFF9CA3AFu,
            TextDisabled: 0x80374151u,
            TextOnAccent: 0xFFFFFFFFu,
            Shadow: 0x40000000u,
            InputSurface: 0xFFFFFFFFu,
            InputSurfaceHover: 0xFFF3F4F6u,
            OnStatusText: 0xFFFFFFFFu,
            RowSubtleText: 0xFF1E1B4Bu,
            HunkOutline: 0xFF3B82F6u,
            Selection: 0xFFCBD5E1u,
            Placeholder: WithAlpha(textMutedLight, 0x80),
            CheckboxBorderIdle: 0xFFD1D5DBu,
            CheckboxDisabledFill: 0xFFE5E7EBu,
            ScrollBarTrackBg: 0xFFF3F4F6u);

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
            DialogWarning: 0xFFCA8A04u,
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

        // Editor-native foregrounds over the light surface (VS Code Light+ lineage). Operator
        // and punctuation stay near the body text color so structure reads without noise.
        var diffSyntax = new DiffSyntaxPalette(
            Keyword: 0xFF0000FFu,
            String: 0xFFA31515u,
            Comment: 0xFF008000u,
            Number: 0xFF098658u,
            Type: 0xFF267F99u,
            Function: 0xFF795E26u,
            Variable: 0xFF001080u,
            Operator: 0xFF383838u,
            Punctuation: 0xFF6E6E6Eu,
            Constant: 0xFF0070C1u);

        var commitBadge = new CommitBadgePalette(
            LocalBg: 0xFFDBEAFEu,
            RemoteBg: 0xFFEDE9FEu,
            HeadBg: 0xFFFEF3C7u,
            TagBg: 0xFFD1FAE5u,
            Text: p.TextBody,
            BranchInSyncIcon: status.Success,
            BranchDivergedIcon: status.Warning,
            BranchUntrackedIcon: p.TextDisabled);

        return BuildStyles(p, status, banner, tooltip, hunkButton, diffSyntax, commitBadge);
    }
}

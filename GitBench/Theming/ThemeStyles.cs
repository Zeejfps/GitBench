namespace GitBench.Theming;

public sealed partial record ThemeStyles
{
    public required ThemePalette Palette { get; init; }
    public required StatusPalette Status { get; init; }
    public required BannerStyles Banner { get; init; }
    public required HeaderActionButtonStyles HeaderActionButton { get; init; }
    public required LocalChangesContentStyles LocalChangesContent { get; init; }
    public required SubmoduleSectionStyles SubmoduleSection { get; init; }
    public required FileChangesSectionStyles FileChangesSection { get; init; }
    public required FileChangeRowStyles FileChangeRow { get; init; }
    public required DialogFrameStyles DialogFrame { get; init; }
    public required TextInputStyles TextInput { get; init; }
    public required BorderedButtonStyles BorderedButton { get; init; }
    public required DialogIconButtonStyles DialogIconButton { get; init; }
    public required ActionButtonStyles ActionButton { get; init; }
    public required DialogActionButtonStyles DialogActionButton { get; init; }
    public required CheckboxStyles Checkbox { get; init; }
    public required CommitBarStyles CommitBar { get; init; }
    public required ModeSwitcherStyles ModeSwitcher { get; init; }
    public required BranchesHeaderStyles BranchesHeader { get; init; }
    public required GroupHeaderRowStyles GroupHeaderRow { get; init; }
    public required GroupRenameFieldStyles GroupRenameField { get; init; }
    public required RepoBarRowStyles RepoBarRow { get; init; }
    public required BranchesViewStyles BranchesView { get; init; }
    public required RepoBarStyles RepoBar { get; init; }
    public required StatusBarStyles StatusBar { get; init; }
    public required DiffViewStyles DiffView { get; init; }
    public required DiffContentStyles DiffContent { get; init; }
    public required DiffHunkButtonStyles DiffHunkButton { get; init; }
    public required ActionsToolbarStyles ActionsToolbar { get; init; }
    public required SidebarSplitterStyles SidebarSplitter { get; init; }
    public required HistorySplitterStyles HistorySplitter { get; init; }
    public required ScrollBarStyles ScrollBar { get; init; }
    public required TooltipStyles Tooltip { get; init; }
    public required CommitDetailsViewStyles CommitDetailsView { get; init; }
    public required DialogBodyStyles DialogBody { get; init; }
    public required BranchPreviewStyles BranchPreview { get; init; }
    public required ContextMenuStyles ContextMenu { get; init; }
    public required CommitsViewStyles CommitsView { get; init; }
    public required RowSelectionStyles RowSelection { get; init; }

    public static readonly ThemeStyles Dark = BuildDark();
    public static readonly ThemeStyles Light = BuildLight();

    private static uint WithAlpha(uint color, byte alpha) =>
        (color & 0x00FFFFFFu) | ((uint)alpha << 24);

    // Brightens each RGB channel by delta (clamped), preserving alpha. Used to derive a
    // hover shade from a base fill without adding a second color to the palette.
    private static uint Lighten(uint argb, uint delta)
    {
        var a = (argb >> 24) & 0xFF;
        var r = Math.Min(0xFFu, ((argb >> 16) & 0xFF) + delta);
        var g = Math.Min(0xFFu, ((argb >> 8) & 0xFF) + delta);
        var b = Math.Min(0xFFu, (argb & 0xFF) + delta);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    // Linearly interpolates RGB from→to by t (0..1), returning a fully opaque color. Used to
    // derive a saturated shade partway toward an accent without adding a palette slot — t toward
    // the accent gains saturation in every theme, unlike bumping the alpha of a near-white base.
    private static uint Mix(uint from, uint to, double t)
    {
        static uint Lerp(uint a, uint b, double k) => (uint)Math.Round(a + (b - (double)a) * k);
        var r = Lerp((from >> 16) & 0xFF, (to >> 16) & 0xFF, t);
        var g = Lerp((from >> 8) & 0xFF, (to >> 8) & 0xFF, t);
        var b = Lerp(from & 0xFF, to & 0xFF, t);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}

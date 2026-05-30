namespace GitGui;

public sealed partial record ThemeStyles
{
    public required ThemePalette Palette { get; init; }
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
    public required CheckboxStyles Checkbox { get; init; }
    public required CommitBarStyles CommitBar { get; init; }
    public required ModeSwitcherStyles ModeSwitcher { get; init; }
    public required BranchesHeaderStyles BranchesHeader { get; init; }
    public required GroupHeaderRowStyles GroupHeaderRow { get; init; }
    public required GroupRenameFieldStyles GroupRenameField { get; init; }
    public required RepoBarRowStyles RepoBarRow { get; init; }
    public required BranchesViewStyles BranchesView { get; init; }
    public required RepoBarStyles RepoBar { get; init; }
    public required DiffViewStyles DiffView { get; init; }
    public required DiffContentStyles DiffContent { get; init; }
    public required DiffHunkButtonStyles DiffHunkButton { get; init; }
    public required ActionsToolbarStyles ActionsToolbar { get; init; }
    public required SidebarSplitterStyles SidebarSplitter { get; init; }
    public required HistorySplitterStyles HistorySplitter { get; init; }
    public required ScrollBarStyles ScrollBar { get; init; }
    public required TooltipStyles Tooltip { get; init; }
    public required OperationRowStyles OperationRow { get; init; }
    public required CommitDetailsViewStyles CommitDetailsView { get; init; }
    public required DialogBodyStyles DialogBody { get; init; }
    public required BranchPreviewStyles BranchPreview { get; init; }
    public required ContextMenuStyles ContextMenu { get; init; }
    public required OperationsStatusBarStyles OperationsStatusBar { get; init; }
    public required CommitsViewStyles CommitsView { get; init; }

    public static readonly ThemeStyles Dark = BuildDark();
    public static readonly ThemeStyles Light = BuildLight();

    private static uint WithAlpha(uint color, byte alpha) =>
        (color & 0x00FFFFFFu) | ((uint)alpha << 24);
}

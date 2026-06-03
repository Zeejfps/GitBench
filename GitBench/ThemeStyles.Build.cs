namespace GitGui;

public partial record ThemeStyles
{
    private static ThemeStyles BuildStyles(
        ThemePalette p,
        StatusPalette status,
        BannerStyles banner,
        TooltipPalette tooltip,
        DiffHunkButtonPalette hunkButton,
        DiffSyntaxPalette diffSyntax,
        CommitBadgePalette commitBadge) =>
        new()
        {
            Palette = p,
            Banner = banner,
            HeaderActionButton = BuildHeaderActionButton(p),
            LocalChangesContent = BuildLocalChangesContent(p),
            SubmoduleSection = BuildSubmoduleSection(p, status),
            FileChangesSection = BuildFileChangesSection(p),
            FileChangeRow = BuildFileChangeRow(p, status),
            DialogFrame = BuildDialogFrame(p, status),
            TextInput = BuildTextInput(p),
            BorderedButton = BuildBorderedButton(p),
            DialogIconButton = BuildDialogIconButton(p),
            ActionButton = BuildActionButton(p),
            DialogActionButton = BuildDialogActionButton(p, status),
            Checkbox = BuildCheckbox(p),
            CommitBar = BuildCommitBar(p),
            ModeSwitcher = BuildModeSwitcher(p),
            BranchesHeader = BuildBranchesHeader(p),
            GroupHeaderRow = BuildGroupHeaderRow(p),
            GroupRenameField = BuildGroupRenameField(p),
            RepoBarRow = BuildRepoBarRow(p, status),
            BranchesView = BuildBranchesView(p, status),
            RepoBar = BuildRepoBar(p),
            StatusBar = BuildStatusBar(p),
            DiffView = BuildDiffView(p, status),
            DiffContent = BuildDiffContent(p, status, diffSyntax),
            DiffHunkButton = BuildDiffHunkButton(hunkButton),
            ActionsToolbar = BuildActionsToolbar(p, status),
            SidebarSplitter = BuildSidebarSplitter(p),
            HistorySplitter = BuildHistorySplitter(p),
            ScrollBar = BuildScrollBar(p),
            Tooltip = BuildTooltip(p, tooltip),
            OperationRow = BuildOperationRow(p, status),
            CommitDetailsView = BuildCommitDetailsView(p),
            DialogBody = BuildDialogBody(p),
            BranchPreview = BuildBranchPreview(status),
            ContextMenu = BuildContextMenu(p),
            OperationsStatusBar = BuildOperationsStatusBar(p),
            CommitsView = BuildCommitsView(p, commitBadge),
        };
}

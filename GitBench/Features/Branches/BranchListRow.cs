using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

// One branch-sidebar row: composes the shared TreeRow with the kind's chevron, icon, name, and
// ahead/behind badge, driven by its BranchRow descriptor. The descriptor carries the structural and
// listing-derived data (so a reload remounts only the rows that changed); transient state — selection,
// busy, pending-head, worktree, hover — binds live off the view model so it repaints without a rebuild.
// Selectable rows register their live rect with the shared selection bar.
internal sealed record BranchListRow : Widget<BranchRowState>
{
    // Branch/folder/stash rows match the repo bar's nested rows; section headers match its shorter
    // group-header row so LOCAL / REMOTES / STASHES sit at the same height as the group names.
    private const float ContentRowHeight = 26f;

    protected override BranchRowState CreateState(Context ctx) =>
        new(ctx.Require<BranchRow>(), ctx.Require<BranchesViewModel>());

    protected override IWidget Build(Context ctx, BranchRowState state)
    {
        var row = ctx.Require<BranchRow>();
        var vm = ctx.Require<BranchesViewModel>();
        var bar = ctx.Require<TreeSelectionBar<BranchRowKey>>();
        var theme = ctx.Theme();
        var key = row.SelectionKey;

        BranchesViewStyles BV() => theme.Styles.Value.BranchesView;
        RowSelectionStyles RS() => theme.Styles.Value.RowSelection;
        bool Selected() => key is { } k && BranchRowKey.From(vm.Selection.Value) == k;
        bool Busy() => row is LocalBranchRow lb && vm.BusyBranch.Value == lb.Name;
        bool Head() => row is LocalBranchRow lb && (vm.PendingHead.Value is { } ph ? lb.Name == ph : lb.IsHead);
        bool Worktree() => row is LocalBranchRow lb && vm.WorktreeBranches.Value.Contains(lb.Name);

        var treeRow = new TreeRow
        {
            Depth = row.Depth,
            RowHeight = IsSectionHeader(row) ? Sizes.RowHeight : ContentRowHeight,
            Guides = new TreeGuides(row.GuideMask, IsSectionHeader(row) ? 0 : row.Depth + 1),
            Chevron = ChevronFor(row, ctx),
            Glyph = GlyphFor(row),
            GlyphSize = 13f,
            IconColor = Prop.Bind(() =>
            {
                var bv = BV();
                switch (row)
                {
                    case FolderRow: return bv.SectionHeaderText;
                    case StashRow: return Selected() ? RS().Text : bv.RowText;
                    case LocalBranchRow lb:
                        if (Busy() && !Head()) return bv.RowTextDim;
                        return lb.Upstream switch
                        {
                            BranchUpstreamKind.Tracked => bv.AheadColor,
                            BranchUpstreamKind.Gone => bv.BehindColor,
                            _ => bv.RowTextDim,
                        };
                    case RemoteBranchRow: return Selected() ? RS().Text : bv.RowText;
                    default: return bv.RowText;
                }
            }),
            Name = NameFor(row, ctx),
            NameColor = Prop.Bind(() =>
            {
                var bv = BV();
                if (Selected()) return RS().Text;
                if (row is LocalHeaderRow or RemotesHeaderRow or StashesHeaderRow or RemoteHeaderRow)
                    return bv.SectionHeaderText;
                if (Head()) return bv.HeadIdleText;
                if (Busy() || Worktree()) return bv.RowTextDim;
                return bv.RowText;
            }),
            NameWeight = Prop.Bind(() => Head() ? FontWeight.Bold : FontWeight.Normal),
            // Section headers (LOCAL / REMOTES / STASHES) match the repo bar's group headers: caption
            // sized, indented to the group-header indent (Hair, outdented from content), with a
            // between-section gap above all but the first (LOCAL is always first).
            NameSize = IsSectionHeader(row) ? FontSize.Caption : default,
            IndentOverride = IsSectionHeader(row) ? Spacing.Hair : null,
            SpacingBefore = row is RemotesHeaderRow or StashesHeaderRow ? Spacing.Lg : 0,
            Background = Prop.Bind(() =>
                !Selected() && (state.Hovered.Value || state.ContextHighlighted.Value) ? RS().FillHover : 0u),
            Trailing = TrailingFor(row),
        };

        return key is { } selKey
            ? treeRow.Use(view => bar.Register(selKey, () => view.Position))
            : treeRow;
    }

    private static bool IsSectionHeader(BranchRow row) =>
        row is LocalHeaderRow or RemotesHeaderRow or StashesHeaderRow;

    // Collapsible rows show a fold chevron (the whole row toggles, so it's a plain glyph); leaf rows
    // return null and TreeRow reserves the column so their icons stay aligned under sibling folders.
    private static IWidget? ChevronFor(BranchRow row, Context ctx)
    {
        if (row is not ICollapsibleRow col) return null;
        var glyph = col.IsOpen
            ? LucideIcons.ChevronDown
            : Direction.IsRtl(ctx) ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight;
        return new Text
        {
            Value = glyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Caption,
            // Section headers carry the repo bar's group-header chevron column (icon-wide) so their
            // names line up with the group names; folders/remote headers keep the content chevron width.
            Width = IsSectionHeader(row) ? Sizes.Icon : TreeMetrics.ChevronWidth,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.BranchesView.SectionHeaderText),
        };
    }

    private static string? GlyphFor(BranchRow row) => row switch
    {
        FolderRow f => f.IsOpen ? LucideIcons.FolderOpen : LucideIcons.Folder,
        StashRow => LucideIcons.Stash,
        LocalBranchRow lb => lb.Upstream == BranchUpstreamKind.Gone ? LucideIcons.CloudOff : LucideIcons.Branch,
        RemoteBranchRow => LucideIcons.Branch,
        _ => null,
    };

    private static Prop<string?> NameFor(BranchRow row, Context ctx)
    {
        var loc = ctx.Localization();
        return row switch
        {
            LocalHeaderRow => Prop.Bind<string?>(() => loc.Strings.Value.BranchesSectionLocal.ToUpperInvariant()),
            RemotesHeaderRow => Prop.Bind<string?>(() => loc.Strings.Value.BranchesSectionRemote.ToUpperInvariant()),
            StashesHeaderRow => Prop.Bind<string?>(() => loc.Strings.Value.BranchesSectionStashes.ToUpperInvariant()),
            RemoteHeaderRow r => r.RemoteName,
            FolderRow f => f.DisplayName,
            LocalBranchRow b => b.DisplayName,
            RemoteBranchRow b => b.DisplayName,
            StashRow s => s.DisplayName,
            _ => string.Empty,
        };
    }

    private static IWidget? TrailingFor(BranchRow row)
    {
        if (row is not LocalBranchRow { Sync: { } sync }) return null;
        if (sync is { Ahead: 0, Behind: 0 }) return null;

        var groups = new List<IWidget>(2);
        if (sync.Ahead > 0) groups.Add(BadgeGroup(LucideIcons.Push, sync.Ahead, s => s.BranchesView.AheadColor));
        if (sync.Behind > 0) groups.Add(BadgeGroup(LucideIcons.Pull, sync.Behind, s => s.BranchesView.BehindColor));
        return new Row { Gap = 8f, CrossAxis = CrossAxisAlignment.Center, Children = groups.ToArray() };
    }

    private static IWidget BadgeGroup(string icon, int count, Func<ThemeStyles, uint> color) => new Row
    {
        Gap = 2f,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = icon,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Caption,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(color),
            },
            new Text
            {
                Value = count.ToString(),
                FontSize = FontSize.Caption,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(color),
            },
        ],
    };
}

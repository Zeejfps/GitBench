using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed class RailFolderState(RailSectionViewModel vm, Func<IReadOnlyList<RepoBarContextMenu.Item>> menu) : INavigableRow
{
    public State<bool> Hovered { get; } = new(false);
    public ICommand Activate => vm.ToggleCollapsed;
    public IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems() => menu();
}

// The rail's stand-in for a group header. Expanded, it is a folder glyph in the group's color
// above the repo tiles; collapsed, it previews up to four of the group's repos as a 2×2 grid of
// swatches and takes over the active ring and status dot for whatever it hides.
internal sealed record RepoRailFolderTile : Widget<RailFolderState>
{
    private const int PreviewSlots = 4;
    private const int PreviewSize = 14;
    private const int PreviewGap = 2;
    private const float GlyphSize = 18f;

    protected override RailFolderState CreateState(Context ctx)
    {
        var vm = ctx.Require<RailSectionViewModel>();
        return new RailFolderState(vm, () => GroupHeaderRow.BuildMenuItems(ctx, vm.HeaderVm));
    }

    protected override IWidget Build(Context ctx, RailFolderState state)
    {
        var vm = ctx.Require<RailSectionViewModel>();
        var identity = RepoRailTile.IdentityColor(vm.Group.Id);

        var showsPreview = new Derived<bool>(() => !vm.IsExpanded.Value && vm.Primaries.Count > 0);
        var face = new Box
        {
            Width = RepoRailTile.TileSize,
            Height = RepoRailTile.TileSize,
            Children =
            [
                new Switch<bool>
                {
                    Value = showsPreview,
                    Case = preview => preview ? Preview(vm) : Glyph(vm, identity),
                },
            ],
        };

        var isActive = new Derived<bool>(() => !vm.IsExpanded.Value && vm.ContainsActive.Value);
        var ring = RepoRailTile.Ring(face, isActive, state.Hovered);
        var statusDot = RepoRailTile.StatusDot(() => vm.IsExpanded.Value ? RepoRowBadge.None : vm.Badge.Value);
        var tooltipText = new Derived<string?>(() => vm.Group.Name.Value);

        return RepoRailTile.Compose(ring, statusDot)
            .Use(_ => showsPreview)
            .Use(_ => isActive)
            .Use(_ => tooltipText)
            .Use(view => new Tooltip(view, ctx, tooltipText, state.Hovered));
    }

    private static IWidget Glyph(RailSectionViewModel vm, uint identity) => new Text
    {
        Value = Prop.Bind<string?>(() => vm.IsExpanded.Value ? LucideIcons.FolderOpen : LucideIcons.Folder),
        FontFamily = LucideIcons.FontFamily,
        FontSize = GlyphSize,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(_ => identity),
    };

    private static IWidget Preview(RailSectionViewModel vm) => new Column
    {
        MainAxis = MainAxisAlignment.Center,
        CrossAxis = CrossAxisAlignment.Center,
        Gap = PreviewGap,
        Children =
        [
            PreviewRow(vm, 0),
            PreviewRow(vm, PreviewSlots / 2),
        ],
    };

    private static IWidget PreviewRow(RailSectionViewModel vm, int firstSlot) => new Row
    {
        Gap = PreviewGap,
        Children = [PreviewSlot(vm, firstSlot), PreviewSlot(vm, firstSlot + 1)],
    };

    private static IWidget PreviewSlot(RailSectionViewModel vm, int index)
    {
        var repo = new Derived<RepoNodeViewModel?>(() => index < vm.Primaries.Count ? vm.Primaries[index] : null);
        return new Switch<RepoNodeViewModel?>
        {
            Value = repo,
            Case = node => node is null
                ? new Box { Width = PreviewSize, Height = PreviewSize }
                : Swatch(node),
        }.Use(_ => repo);
    }

    private static IWidget Swatch(RepoNodeViewModel node) => new Box
    {
        Width = PreviewSize,
        Height = PreviewSize,
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Background = Theme.Color(s => node.IsMissing.Value
            ? s.Palette.SurfaceHoverStrong
            : node.CustomColor.Value ?? RepoRailTile.IdentityColor(node.RepoId)),
        Children =
        [
            new Switch<string?>
            {
                Value = node.CustomIconPath,
                Case = path => path is null
                    ? Empty.Widget
                    : new RepoIconImage { Path = path, Size = PreviewSize },
            },
        ],
    };
}

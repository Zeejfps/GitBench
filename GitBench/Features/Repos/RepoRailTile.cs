using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// One collapsed-rail tile: the repo's initials on its identity color, an active/hover ring, the
// hotkey digit, and the same status dot the rows show. Shares RepoRowState with the row variants,
// so the parent attaches NavigableRowController to drive hover, activation, and the context menu.
internal sealed record RepoRailTile : Widget<RepoRowState>
{
    internal const int TileSize = 36;
    internal const int RingThickness = 2;
    internal const int RingGap = 2;
    internal const int RingInset = RingThickness + RingGap;

    private static readonly char[] NameSeparators = [' ', '-', '_', '.'];

    protected override RepoRowState CreateState(Context ctx) => new(ctx.Require<RepoNodeViewModel>());

    protected override IWidget Build(Context ctx, RepoRowState state)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var identityColor = IdentityColor(vm.RepoId);

        var tile = new Box
        {
            Width = TileSize,
            Height = TileSize,
            BorderRadius = BorderRadiusStyle.All(Radius.Lg),
            Background = Theme.Color(s => vm.IsMissing.Value ? s.Palette.SurfaceHoverStrong : identityColor),
            Children =
            [
                new Text
                {
                    Value = Prop.Bind<string?>(() => Initials(vm.DisplayName.Value)),
                    FontSize = FontSize.Body,
                    Weight = FontWeight.Bold,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => vm.IsMissing.Value ? s.Palette.TextDisabled : s.Palette.TextOnAccent),
                },
                new Switch<string?>
                {
                    Value = vm.CustomIconPath,
                    Case = path => path is null
                        ? Empty.Widget
                        : new RepoIconImage { Path = path, Size = TileSize },
                },
            ],
        };

        var ring = Ring(tile, vm.IsActive, state.Hovered);

        var hotkeyBadge = new Box
        {
            Width = 15,
            Height = 15,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Visible = Prop.Bind(() => vm.HotkeyDigit.Value is not null),
            Children =
            [
                new Text
                {
                    Value = Prop.Bind<string?>(() => vm.HotkeyDigit.Value?.ToString() ?? string.Empty),
                    FontSize = FontSize.Caption,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.TextStrong),
                },
            ],
        };

        var statusDot = StatusDot(() => vm.Badge.Value);

        // The tooltip wants a nullable text readable; the widget owns the Derived, Use ties both
        // to the view's lifetime.
        var tooltipText = new Derived<string?>(() => vm.DisplayName.Value);

        return Compose(ring, statusDot, hotkeyBadge)
            .Use(_ => tooltipText)
            .Use(view => new Tooltip(view, ctx, tooltipText, state.Hovered));
    }

    internal static IWidget Ring(IWidget face, IReadable<bool> isActive, IReadable<bool> isHovered) => new Box
    {
        BorderSize = BorderSizeStyle.All(RingThickness),
        BorderRadius = BorderRadiusStyle.All(Radius.Lg + RingInset),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(
            isActive.Value ? s.RowSelection.AccentBar
            : isHovered.Value ? s.Palette.BorderStrong
            : 0u)),
        Children =
        [
            new Padding
            {
                Amount = PaddingStyle.All(RingGap),
                Children = [face],
            },
        ],
    };

    internal static IWidget StatusDot(Func<RepoRowBadge> badge) => new Box
    {
        Width = 8,
        Height = 8,
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Background = Theme.Color(s => badge() == RepoRowBadge.Error
            ? s.RepoBarRow.BadgeError
            : s.RepoBarRow.BadgeDirty),
        Visible = Prop.Bind(() => badge() != RepoRowBadge.None),
    };

    internal static IWidget Compose(IWidget ring, IWidget statusDot, IWidget? cornerBadge = null) => new Stack
    {
        Children =
        [
            ring,
            new Padding
            {
                Amount = PaddingStyle.All(RingThickness),
                Children =
                [
                    new Column
                    {
                        MainAxis = MainAxisAlignment.Start,
                        CrossAxis = CrossAxisAlignment.End,
                        Children = [statusDot],
                    },
                ],
            },
            new Column
            {
                MainAxis = MainAxisAlignment.End,
                CrossAxis = CrossAxisAlignment.End,
                Children = [cornerBadge ?? Empty.Widget],
            },
        ],
    };

    internal static uint IdentityColor(Guid id) => CategoricalPalette.Avatar(Hash(id));

    // "web-frontend" → "WF", "GitBench" → "Gi": two word initials when the name splits, else the
    // first two characters so single-word names don't shout a double capital.
    internal static string Initials(string name)
    {
        var parts = name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length >= 2)
            return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
        var word = parts[0];
        return word.Length >= 2
            ? string.Concat(char.ToUpperInvariant(word[0]), word[1])
            : char.ToUpperInvariant(word[0]).ToString();
    }

    // Guid-value hash (string.GetHashCode is randomized per process) so a repo keeps its color
    // across launches without persisting anything.
    private static int Hash(Guid id)
    {
        var h = 0;
        foreach (var b in id.ToByteArray()) h = unchecked(h * 31 + b);
        return h;
    }
}

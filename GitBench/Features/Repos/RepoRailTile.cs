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
    private const int RingThickness = 2;
    private const int RingGap = 2;

    private static readonly IReadable<bool> AlwaysEnabled = new State<bool>(true);
    private static readonly char[] NameSeparators = [' ', '-', '_', '.'];

    protected override RepoRowState CreateState(Context ctx) => new(ctx.Require<RepoNodeViewModel>());

    protected override IWidget Build(Context ctx, RepoRowState state)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        var identityColor = CategoricalPalette.Avatar(Hash(vm.RepoId));

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
            ],
        };

        var ring = new Box
        {
            BorderSize = BorderSizeStyle.All(RingThickness),
            BorderRadius = BorderRadiusStyle.All(Radius.Lg + RingThickness + RingGap),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(
                vm.IsActive.Value ? s.RowSelection.AccentBar
                : state.Hovered.Value ? s.Palette.BorderStrong
                : 0u)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(RingGap),
                    Children = [tile],
                },
            ],
        };

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

        var statusDot = new Box
        {
            Width = 8,
            Height = 8,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Theme.Color(s => vm.Badge.Value == RepoRowBadge.Error
                ? s.RepoBarRow.BadgeError
                : s.RepoBarRow.BadgeDirty),
            Visible = Prop.Bind(() => vm.Badge.Value != RepoRowBadge.None),
        };

        // The tooltip wants a nullable text readable; the widget owns the Derived, Use ties both
        // to the view's lifetime.
        var tooltipText = new Derived<string?>(() => vm.DisplayName.Value);

        return new Stack
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
                    Children = [hotkeyBadge],
                },
            ],
        }
            .Use(_ => tooltipText)
            .Use(view => new Tooltip(view, ctx, tooltipText, state.Hovered, AlwaysEnabled));
    }

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

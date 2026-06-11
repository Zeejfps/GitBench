using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Shared layout for nested rows in the RepoBar (worktrees and submodules). Renders a
// deep-indented row with a small accent-tinted icon and the repo's DisplayName. The
// glyph and accent color are the only visual differences between the two kinds; menu
// items and the optional activation guard are supplied by the subclass.
public abstract record NestedRepoRow : Widget
{
    public required Repo Repo { get; init; }
    // Nesting level under the primary (1 = direct child). Each level adds one indent step;
    // a submodule that itself has submodules also gets a chevron in its slot to fold them.
    public required int Depth { get; init; }

    protected abstract string IconGlyph { get; }
    protected abstract uint SelectAccent(RepoBarRowStyles s);
    protected abstract IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(Context ctx);
    protected virtual Func<bool>? CanActivate => null;

    protected override View CreateView(Context ctx)
    {
        var repo = Repo;
        var registry = ctx.Require<IRepoRegistry>();
        var status = ctx.Require<IRepoStatusStore>();
        var theme = ctx.Theme();

        var isHovered = new State<bool>(false);

        var icon = new TextView(ctx.Canvas)
        {
            Text = IconGlyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 13,
            Width = RepoBar.RowIconWidth,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindTextColor(() => repo.IsMissing
            ? theme.Styles.Value.RepoBarRow.TextMissing
            : SelectAccent(theme.Styles.Value.RepoBarRow));

        var label = new TextView(ctx.Canvas)
        {
            Text = repo.DisplayName,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        RowChrome.BindRowText(theme, label, registry, repo);

        // Each nesting level shifts the row one indent step right. The row then lays out exactly
        // like a primary (chevron, icon, label), so the chevron column lines up per level and a
        // submodule with its own submodules can fold them. At depth 1 the icon lands in the same
        // place the old fixed indent put it, so existing one-level layouts are unchanged.
        var leftPad = RepoBar.RowPaddingLeft + (int)TreeMetrics.IndentLevel * Depth;

        var chevronSlot = new WorktreeChevron { Repo = repo }.BuildView(ctx);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = new PaddingStyle { Left = leftPad, Right = 12 },
            Children =
            {
                new FlexRowView
                {
                    Gap = RepoBar.RowIconGap,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        chevronSlot,
                        icon,
                        new FlexItem { Grow = 1, Child = label },
                        RowChrome.CreateBadge(theme, status, repo.Id),
                    }
                }
            }
        };
        RowChrome.BindRowBackground(theme, background, isHovered, registry, repo.Id);

        var root = new ContainerView { Height = 26 };
        root.Children.Add(background);

        root.UseController(ctx.Require<InputSystem>(), () => new NavigableRowController(
            ctx,
            repo.Id,
            registry,
            h => isHovered.Value = h,
            _ => BuildMenuItems(ctx),
            canActivate: CanActivate));
        return root;
    }

    protected static Repo? FindRepo(IRepoRegistry registry, Guid id)
    {
        foreach (var r in registry.Repos)
        {
            if (r.Id == id) return r;
        }
        return null;
    }
}

using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

// Shared layout for nested rows in the RepoBar (worktrees and submodules). Renders a
// deep-indented row with a small accent-tinted icon and the repo's DisplayName. The
// glyph and accent color are the only visual differences between the two kinds; menu
// items and the optional activation guard are supplied by the subclass.
public abstract class NestedRepoRow : MultiChildView
{
    protected NestedRepoRow(
        Repo repo,
        IRepoRegistry registry,
        IRepoStatusStore status,
        string iconGlyph,
        Func<RepoBarRowStyles, uint> accentSelector,
        Func<Context, IReadOnlyList<RepoBarContextMenu.Item>> buildMenuItems,
        // Nesting level under the primary (1 = direct child). Each level adds one indent step;
        // a submodule that itself has submodules also gets a chevron in its slot to fold them.
        int depth,
        Func<bool>? canActivate = null)
    {
        Height = 26;

        var isHovered = new State<bool>(false);

        var icon = new TextView
        {
            Text = iconGlyph,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 13,
            Width = RepoBar.RowIconWidth,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => repo.IsMissing
            ? s.RepoBarRow.TextMissing
            : accentSelector(s.RepoBarRow));

        var label = new TextView
        {
            Text = repo.DisplayName,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        RowChrome.BindRowText(label, registry, repo);

        // Each nesting level shifts the row one indent step right. The row then lays out exactly
        // like a primary (chevron, icon, label), so the chevron column lines up per level and a
        // submodule with its own submodules can fold them. At depth 1 the icon lands in the same
        // place the old fixed indent put it, so existing one-level layouts are unchanged.
        var leftPad = RepoBar.RowPaddingLeft + (int)TreeMetrics.IndentLevel * depth;

        var chevronSlot = new WorktreeChevron(repo, registry);

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
                        RowChrome.CreateBadge(status, repo.Id),
                    }
                }
            }
        };
        RowChrome.BindRowBackground(background, isHovered, registry, repo.Id);
        AddChildToSelf(background);

        this.UseController(ctx => new NavigableRowController(
            ctx,
            repo.Id,
            registry,
            h => isHovered.Value = h,
            _ => buildMenuItems(ctx),
            canActivate: canActivate));
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

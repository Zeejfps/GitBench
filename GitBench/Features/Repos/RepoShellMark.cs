using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Marks a repository with a terminal whose shell is still running.
/// </summary>
internal sealed record RepoShellMark : Widget
{
    public required IReadable<bool> When { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = LucideIcons.SquareTerminal,
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        Width = RepoRowShell.TrailingWidth,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.RepoBarRow.BadgeShell),
        Visible = Prop.Bind(When),
    };
}

using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// The RepoBar row's trailing decoration while git work is outstanding for that repo: a rotating
/// loader occupying the same width as the status dot it stands in for, so a row doesn't shift when
/// the load resolves. Its angle comes from the bar's single spinner, so every loading row turns in
/// phase.
/// </summary>
internal sealed record RepoRowSpinner : Widget
{
    protected override IWidget Build(Context ctx) => new Text
    {
        Value = LucideIcons.Loader,
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        Width = RepoRowShell.TrailingWidth,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.RepoBarRow.BadgeLoading),
        Rotation = Prop.Bind(ctx.Require<RepoBarViewModel>().LoadRotation),
    };
}

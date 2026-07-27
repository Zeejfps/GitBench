using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// The branch row's ahead/behind badge while an operation is moving its counts: a rotating loader
/// sized to the badge it stands in for. Its angle comes from the sidebar's single spinner, so every
/// spinning row turns in phase.
/// </summary>
internal sealed record BranchSyncSpinner : Widget
{
    protected override IWidget Build(Context ctx) => new Text
    {
        Value = LucideIcons.Loader,
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        Width = FontSize.Body,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.BranchesView.SectionHeaderText),
        Rotation = Prop.Bind(ctx.Require<BranchesViewModel>().SyncRotation),
    };
}

using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

/// <summary>
/// The check that stands where a branch row's ahead/behind badge was, for the beat between an
/// operation finishing and the refreshed counts arriving. It fades in, holds, and fades out over the
/// sidebar's shared mark timeline, which also unmounts it — so a row that remounts mid-mark (as it
/// does when the refresh lands) picks the fade up where it is rather than starting over.
/// </summary>
internal sealed record BranchSyncMark : Widget
{
    // Fractions of the mark's lifetime spent fading in and out; the rest holds at full opacity.
    private const float FadeIn = 0.12f;
    private const float FadeOut = 0.3f;

    protected override View CreateView(Context ctx)
    {
        var view = new Text
        {
            Value = LucideIcons.Check,
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Caption,
            Width = FontSize.Body,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            // The sidebar's green — the same one the ahead count wears.
            Color = Theme.Color(s => s.BranchesView.AheadColor),
        }.BuildView(ctx);

        view.Bind(ctx.Require<BranchesViewModel>().SyncMarkProgress, p => view.Opacity = Opacity(p));
        return view;
    }

    private static float Opacity(float t)
    {
        if (t < FadeIn) return t / FadeIn;
        if (t > 1f - FadeOut) return (1f - t) / FadeOut;
        return 1f;
    }
}

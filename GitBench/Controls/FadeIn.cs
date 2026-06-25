using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// Plays a one-shot enter animation on its child when it mounts — the child fades in (and, if a
/// non-zero <see cref="Rise"/> is set, drifts up into place) — then the tween parks and stops driving
/// the render loop. Because a <see cref="Switch{T}"/> branch mounts fresh each time it becomes shown,
/// wrapping the branch makes the animation replay on appearance (a repo's content arriving) yet not
/// on an in-place refresh of already-shown content.
/// </summary>
internal sealed record FadeIn : Widget
{
    public required IWidget Child { get; init; }
    public float Duration { get; init; } = Transitions.ContentEnterSeconds;
    public float Rise { get; init; }

    /// <summary>For a loading placeholder rather than content: opacity rides an ease-in curve so it
    /// stays faint early and a fast load swaps it out before it registers. Defaults to a content fade
    /// (even linear alpha, decelerating drift).</summary>
    public bool Bloom { get; init; }

    protected override View CreateView(Context ctx)
    {
        var view = Child.BuildView(ctx);
        var tween = new Tween(ctx.Require<IFrameTicker>(), Duration,
            Bloom ? Easings.EaseInCubic : Easings.EaseOutCubic);

        // Content fades on the raw linear progress (an even fade); a bloom rides the eased curve so
        // its alpha stays low early. The drift always decelerates into place on the eased progress.
        view.Bind(Bloom ? tween.Progress : tween.LinearProgress, o => view.Opacity = o);
        if (Rise != 0f)
            view.Bind(tween.Progress, p => view.TranslationY = Rise * (1f - p));

        view.Use(() => tween);
        tween.Play();
        return view;
    }
}

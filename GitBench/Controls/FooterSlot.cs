using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// An animated footer slot: holds at most one piece of content, keyed by an int kind (0 = nothing). It
/// only ever shows one thing at a time, so every change is a slide: appearing slides up from the bottom
/// edge, disappearing slides back down, and switching from one content to another (e.g. the commit bar
/// to a merge bar when an operation starts) hides the old one before showing the new — never a morph
/// that leaves the bar standing in place. The slide drives the layout above it so the footer pushes
/// content up as it grows. The incoming content tracks its own live height each frame, so a commit
/// message that finishes loading mid-slide grows the bar instead of snapping it. Each kind is built
/// once and reattached on later shows, preserving its edit state and focus-ring registration.
/// </summary>
internal sealed record FooterSlot : Widget
{
    public required IReadable<int> Kind { get; init; }
    public required Func<int, IWidget> Content { get; init; }
    public float Duration { get; init; } = 0.16f;

    protected override View CreateView(Context ctx)
    {
        // Clamp the per-frame step so the heavy frame a repo switch lands on can't hand the slide one
        // huge dt and leap it to the end.
        var tween = new Tween(ctx.Require<IFrameTicker>(), Duration, Easings.EaseOutCubic, maxStep: 0.033f);
        var view = new FooterSlotView(ctx, tween, Content);
        view.Bind(tween.Progress, view.OnProgress);
        view.Bind(Kind, view.SetKind);
        view.Use(() => tween);
        return view;
    }

    private sealed class FooterSlotView : View
    {
        private readonly Context _ctx;
        private readonly Tween _tween;
        private readonly Func<int, IWidget> _content;
        private readonly Dictionary<int, View> _byKind = new();

        private View? _child;
        private int _shownKind;
        private int _targetKind;
        private bool _animating;
        private bool _collapsing;
        private float _startHeight;
        private float _height;

        public FooterSlotView(Context ctx, Tween tween, Func<int, IWidget> content)
        {
            _ctx = ctx;
            _tween = tween;
            _content = content;
            _tween.Completed += OnComplete;
        }

        public override bool ClipsContent => true;

        public void SetKind(int kind)
        {
            if (kind == _targetKind) return;
            _targetKind = kind;
            // Mid-slide: let the current phase land; OnComplete re-checks the target and continues.
            if (!_animating) Step();
        }

        // Drive one phase toward the target. Called only when settled (from SetKind, or from OnComplete
        // once a phase has just finished). A swap collapses what's on screen first, then expands the new
        // content — so OnComplete may call Step again to start the second half.
        private void Step()
        {
            if (_shownKind == _targetKind) return;

            if (_child != null)
            {
                _collapsing = true;
                _startHeight = NaturalHeight();
                _height = _startHeight;
            }
            else
            {
                MountChild(_targetKind);
                _collapsing = false;
                _startHeight = 0f;
                _height = 0f;
            }

            _animating = true;
            _tween.Restart();
        }

        public void OnProgress(float p)
        {
            if (!_animating) return;
            if (_collapsing)
            {
                _height = _startHeight * (1f - p);
            }
            else
            {
                // Read the live natural height every frame so content that finishes loading mid-slide
                // grows the target with it rather than landing short and snapping once settled.
                _height = NaturalHeight() * p;
            }
            SetDirty();
        }

        private void OnComplete()
        {
            _animating = false;
            if (_collapsing)
            {
                DropChild();
                _shownKind = 0;
                _collapsing = false;
                _height = 0f;
            }
            else
            {
                _shownKind = _targetKind;
                _height = NaturalHeight();
            }
            SetDirty();
            Step();
        }

        private void MountChild(int kind)
        {
            if (_child != null) RemoveChildFromSelf(_child);
            // Build each kind once and reattach it on later shows: a content that registers focus stops
            // (the normal commit bar shares the file list's ring) must not re-register them every appear,
            // and keeping the view alive preserves its in-flight edit state.
            if (!_byKind.TryGetValue(kind, out var child))
            {
                child = _content(kind).BuildView(_ctx);
                _byKind[kind] = child;
            }
            _child = child;
            AddChildToSelf(_child);
        }

        private void DropChild()
        {
            if (_child != null) RemoveChildFromSelf(_child);
            _child = null;
        }

        private float NaturalHeight()
        {
            if (_child == null) return 0f;
            var width = Position.Width > 0 ? (float)Position.Width : _child.MeasureWidth();
            return _child.MeasureHeight(width);
        }

        // While sliding, the slot reports its lerped height; once settled it tracks the content's live
        // height so layout-driven growth (a wrapping description) keeps fitting. Empty = zero.
        protected override float MeasureHeightIntrinsic(float availableWidth)
        {
            if (_animating) return _height;
            return _child == null ? 0f : _child.MeasureHeight(availableWidth);
        }

        protected override void OnLayoutChildren()
        {
            if (_child == null) return;
            var pos = Position;
            var full = _child.MeasureHeight(pos.Width);
            _child.LeftConstraint = pos.Left;
            _child.WidthConstraint = pos.Width;
            _child.HeightConstraint = full;
            // Pin the child's top to the slot's top at full height: while the slot is shorter it
            // overflows below the bottom edge (clipped), rising into / out of view as the slot animates.
            _child.BottomConstraint = pos.Top - full;
            _child.LayoutSelf();
        }

        protected override void OnDrawChildren(ICanvas c)
        {
            c.PushClip(Position);
            base.OnDrawChildren(c);
            c.PopClip();
        }
    }
}

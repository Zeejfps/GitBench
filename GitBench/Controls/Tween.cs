using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A ticker-driven 0→1 animation. <see cref="Play"/> registers a per-frame tick that advances
/// <see cref="Progress"/> (eased) to 1 over the configured duration; <see cref="Reverse"/> runs it
/// back to 0. The tick unregisters itself once it reaches an end (and on <see cref="Dispose"/>), so
/// a finished animation stops driving the render loop. Mirrors <see cref="SpinnerAnimation"/>'s
/// lifetime; bind render-only view props (Opacity, TranslationX/Y) to <see cref="Progress"/>.
/// </summary>
internal sealed class Tween : IDisposable
{
    private readonly IFrameTicker _ticker;
    private readonly float _duration;
    private readonly float _reverseDuration;
    private readonly float _maxStep;
    private readonly Func<float, float> _ease;
    private readonly Action<float> _tick;
    private readonly State<float> _linear = new(0f);
    private readonly State<float> _progress = new(0f);
    private int _direction;
    private bool _running;

    /// <summary>Eased progress in [0,1] — for motion that should decelerate into place
    /// (<c>TranslationY = anim.Progress.Bind(p => 10f * (1f - p))</c>).</summary>
    public IReadable<float> Progress => _progress;

    /// <summary>Raw, un-eased progress in [0,1] — for a perceptually even fade, where an ease-out
    /// would front-load the alpha and make it read as a pop rather than a fade.</summary>
    public IReadable<float> LinearProgress => _linear;

    public event Action? Completed;

    /// <param name="maxStep">Upper bound (seconds) on the per-frame advance. A long frame (e.g. the UI
    /// thread stalling on a heavy rebuild) otherwise hands the tween a large <c>dt</c> and it leaps to
    /// stay on the wall clock; clamping the step trades a touch of wall-clock duration under load for a
    /// slide that never jumps. Defaults to no clamp.</param>
    /// <param name="reverseDurationSeconds">Duration (seconds) for <see cref="Reverse"/>. Negative
    /// reuses <paramref name="durationSeconds"/> (symmetric). Set it shorter than the forward duration
    /// for an exit that snaps closed faster than it opened.</param>
    public Tween(IFrameTicker ticker, float durationSeconds, Func<float, float>? easing = null, float maxStep = float.PositiveInfinity, float reverseDurationSeconds = -1f)
    {
        _ticker = ticker;
        _duration = MathF.Max(durationSeconds, 0.0001f);
        _reverseDuration = reverseDurationSeconds < 0f ? _duration : MathF.Max(reverseDurationSeconds, 0.0001f);
        _maxStep = maxStep;
        _ease = easing ?? Easings.Linear;
        _tick = Advance;
    }

    public void Play() => Run(+1);
    public void Reverse() => Run(-1);

    /// <summary>Snaps progress back to 0 and plays forward — re-triggers a one-shot animation
    /// (e.g. a selection bar that should slide from a freshly captured start each time the target
    /// changes), whether or not it is currently running.</summary>
    public void Restart()
    {
        _linear.Value = 0f;
        _progress.Value = _ease(0f);
        Run(+1);
    }

    private void Run(int direction)
    {
        _direction = direction;
        if (_running) return; // already ticking — just flipped direction
        _running = true;
        _ticker.Add(_tick);
    }

    private void Advance(float dt)
    {
        var step = MathF.Min(dt, _maxStep);
        var duration = _direction > 0 ? _duration : _reverseDuration;
        var next = Math.Clamp(_linear.Value + _direction * (step / duration), 0f, 1f);
        _linear.Value = next;
        _progress.Value = _ease(next); // State change → bound view setter → repaint next frame

        if ((_direction > 0 && next >= 1f) || (_direction < 0 && next <= 0f))
        {
            _ticker.Remove(_tick); // stop driving the loop once parked at an end
            _running = false;
            Completed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_running) _ticker.Remove(_tick);
        _running = false;
    }
}

using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Drives a continuous breathe for loading skeletons: <see cref="Start"/> registers a per-frame tick
/// that advances <see cref="Value"/> smoothly 0→1→0 (a raised cosine, so it eases at both ends rather
/// than snapping) on a fixed period, until <see cref="Stop"/> or disposal. Sibling to
/// <see cref="SpinnerAnimation"/> — and like it, a running pulse keeps the frame loop awake, so a view
/// must stop it the moment loading ends.
/// </summary>
internal sealed class Pulse : IDisposable
{
    private const float PeriodSeconds = 1.2f;

    private readonly IFrameTicker _ticker;
    private readonly Action<float> _tick;
    private readonly State<float> _value = new(0f);
    private bool _running;
    private float _phase;

    /// <summary>The breathe in [0,1]; rest value is 0 (skeletons keep a faint floor alpha there).</summary>
    public IReadable<float> Value => _value;

    public Pulse(IFrameTicker ticker)
    {
        _ticker = ticker;
        _tick = Advance;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _ticker.Add(_tick);
    }

    public void Stop()
    {
        if (!_running) return;
        _ticker.Remove(_tick);
        _running = false;
    }

    private void Advance(float dt)
    {
        _phase += dt / PeriodSeconds;
        _phase -= MathF.Floor(_phase); // wrap to [0,1), robust to a long frame
        _value.Value = 0.5f - 0.5f * MathF.Cos(_phase * MathF.Tau);
    }

    public void Dispose() => Stop();
}

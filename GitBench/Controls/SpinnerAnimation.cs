using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Drives a continuous rotation for a loading-spinner icon. <see cref="Start"/> registers a
/// per-frame tick that advances <see cref="Rotation"/> until <see cref="Stop"/> is called or
/// the helper is disposed. <see cref="IsActive"/> follows the same lifetime, so views can
/// subscribe once and flip both the icon (busy vs. idle) and its angle from a single source.
/// </summary>
internal sealed class SpinnerAnimation : IDisposable
{
    // One revolution per second. Clockwise on screen = negative angle because the
    // orthographic projection has Y up.
    private const float RotationPerSecond = -MathF.Tau;

    private readonly IFrameTicker _ticker;
    private readonly Action<float> _tick;
    private readonly State<bool> _isActive = new(false);
    private readonly State<float> _rotation = new(0f);

    public IReadable<bool> IsActive => _isActive;
    public IReadable<float> Rotation => _rotation;

    public SpinnerAnimation(IFrameTicker ticker)
    {
        _ticker = ticker;
        _tick = dt => _rotation.Value += RotationPerSecond * dt;
    }

    public void Start()
    {
        if (_isActive.Value) return;
        _rotation.Value = 0f;
        _isActive.Value = true;
        _ticker.Add(_tick);
    }

    public void Stop()
    {
        if (!_isActive.Value) return;
        _ticker.Remove(_tick);
        _rotation.Value = 0f;
        _isActive.Value = false;
    }

    public void Dispose() => Stop();
}

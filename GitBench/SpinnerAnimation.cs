using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Drives a continuous rotation for a loading-spinner icon. <see cref="Start"/> kicks
/// off a UI-thread tick loop that increments <see cref="Rotation"/> until <see cref="Stop"/>
/// is called or the helper is disposed. <see cref="IsActive"/> follows the same lifetime,
/// so views can subscribe once and flip both the icon (busy vs. idle) and its angle from a
/// single source.
/// </summary>
internal sealed class SpinnerAnimation : IDisposable
{
    // Per-frame angle delta. Clockwise on screen = negative angle because the
    // orthographic projection has Y up.
    private const int AnimTickMs = 16;
    private const float RotationPerTick = -MathF.Tau * (AnimTickMs / 1000f);

    private readonly IUiDispatcher _dispatcher;
    private readonly State<bool> _isActive = new(false);
    private readonly State<float> _rotation = new(0f);
    private CancellationTokenSource? _cts;

    public IReadable<bool> IsActive => _isActive;
    public IReadable<float> Rotation => _rotation;

    public SpinnerAnimation(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_isActive.Value) return;
        _cts = new CancellationTokenSource();
        _rotation.Value = 0f;
        _isActive.Value = true;
        RunSpinLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _rotation.Value = 0f;
        _isActive.Value = false;
    }

    public void Dispose() => Stop();

    private void RunSpinLoop(CancellationToken ct)
    {
        var dispatcher = _dispatcher;
        var rotation = _rotation;
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(AnimTickMs, ct).ConfigureAwait(false);
                    dispatcher.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        rotation.Value += RotationPerTick;
                    });
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        }, ct);
    }
}

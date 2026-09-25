using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// Text as it is being written, shown at a steady pace rather than in the lumps it arrives in: what
/// is shown trails the source by a few characters a frame, faster the further behind it is, so a
/// long chunk lands within a moment and a trickle reads as typing. UI thread only.
/// </summary>
internal sealed class StreamReveal : IDisposable
{
    private const float MinCharsPerSecond = 80f;
    private const float CatchUpSeconds = 0.4f;

    private readonly IReadable<string> _source;
    private readonly IFrameTicker _ticker;
    private readonly Action<float> _tick;
    private readonly State<string> _shown;
    private readonly IDisposable _subscription;
    private float _owed;
    private bool _running;

    /// <param name="fromStart">Reveal what the source already holds too, rather than showing it at
    /// once.</param>
    public StreamReveal(IReadable<string> source, IFrameTicker ticker, bool fromStart)
    {
        _source = source;
        _ticker = ticker;
        _tick = Advance;
        _shown = new State<string>(fromStart ? "" : source.Value);
        _subscription = source.Subscribe(_ => Run());
        Run();
    }

    public IReadable<string> Shown => _shown;

    private void Run()
    {
        if (_running || _shown.Value == _source.Value) return;
        _running = true;
        _ticker.Add(_tick);
    }

    private void Advance(float dt)
    {
        var target = _source.Value;
        var shown = _shown.Value;
        var kept = CommonPrefix(shown, target);
        var behind = target.Length - kept;
        if (behind <= 0)
        {
            if (kept != shown.Length) _shown.Value = target;
            Stop();
            return;
        }

        _owed += dt * MathF.Max(MinCharsPerSecond, behind / CatchUpSeconds);
        var step = (int)_owed;
        if (step == 0) return;
        _owed -= step;

        var end = Math.Min(target.Length, kept + step);
        if (end < target.Length && char.IsHighSurrogate(target[end - 1])) end++;
        _shown.Value = target[..end];
        if (end == target.Length) Stop();
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private void Stop()
    {
        _owed = 0f;
        if (!_running) return;
        _running = false;
        _ticker.Remove(_tick);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        Stop();
    }
}

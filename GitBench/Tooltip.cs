using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

public sealed class Tooltip : IDisposable
{
    private const int HoverDelayMs = 500;

    private readonly View _target;
    private readonly Context _context;
    private readonly string _text;
    private readonly IReadable<bool> _isHovered;
    private readonly IReadable<bool> _isEnabled;

    private readonly IDisposable _hoverSub;
    private readonly IDisposable _enabledSub;
    private CancellationTokenSource? _pendingCts;
    private bool _isShown;

    public Tooltip(
        View target,
        Context context,
        string text,
        IReadable<bool> isHovered,
        IReadable<bool> isEnabled)
    {
        _target = target;
        _context = context;
        _text = text;
        _isHovered = isHovered;
        _isEnabled = isEnabled;

        _hoverSub = _isHovered.Subscribe(OnHoverChanged);
        _enabledSub = _isEnabled.Subscribe(OnEnabledChanged);
    }

    public void Dispose()
    {
        _hoverSub.Dispose();
        _enabledSub.Dispose();
        CancelPending();
        HideNow();
    }

    private void OnHoverChanged(bool hovered)
    {
        CancelPending();
        if (hovered && _isEnabled.Value)
        {
            SchedulePending();
        }
        else
        {
            HideNow();
        }
    }

    private void OnEnabledChanged(bool enabled)
    {
        if (enabled) return;
        CancelPending();
        HideNow();
    }

    private void SchedulePending()
    {
        var dispatcher = _context.Get<IUiDispatcher>();
        if (dispatcher == null) return;

        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        var token = cts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HoverDelayMs, token).ConfigureAwait(false);
                dispatcher.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    ShowNow();
                });
            }
            catch (OperationCanceledException)
            {
                // Hover ended before delay elapsed; nothing to do.
            }
        }, token);
    }

    private void CancelPending()
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = null;
    }

    private void ShowNow()
    {
        var service = _context.Get<ITooltipService>();
        if (service == null) return;
        service.Show(this, _text, _target.Position);
        _isShown = true;
    }

    private void HideNow()
    {
        if (!_isShown) return;
        _context.Get<ITooltipService>()?.Hide(this);
        _isShown = false;
    }
}

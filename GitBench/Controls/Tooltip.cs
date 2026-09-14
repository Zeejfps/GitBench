using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.Controls;

public sealed class Tooltip : IDisposable
{
    private const int HoverDelayMs = 500;

    private readonly View _target;
    private readonly Context _context;
    private readonly IReadable<string?> _text;
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
        : this(target, context, (IReadable<string?>)new State<string?>(text), isHovered, isEnabled)
    {
    }

    public Tooltip(
        View target,
        Context context,
        IReadable<string?> text,
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

    // The anchor is placed on screen here, with the coordinates of the window the target sits in:
    // the service is one per app, and a review window's button is not where the main window's
    // origin would put it.
    private void ShowNow()
    {
        var text = _text.Value;
        if (string.IsNullOrEmpty(text)) return;
        var service = _context.Get<ITooltipService>();
        var coordinates = _context.Get<IWindowCoordinates>();
        if (service == null || coordinates == null) return;
        service.Show(this, text, coordinates.ToScreenPoints(CanvasRect.From(_target.Position)));
        _isShown = true;
    }

    private void HideNow()
    {
        if (!_isShown) return;
        _context.Get<ITooltipService>()?.Hide(this);
        _isShown = false;
    }
}

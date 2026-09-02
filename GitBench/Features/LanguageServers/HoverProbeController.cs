using GitBench.Features.Diff;
using GitBench.Lsp;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal sealed class HoverProbeController : KeyboardMouseController, IDisposable
{
    private const int DwellMs = 350;

    private readonly IHoverSurface _surface;
    private readonly IHoverSource _servers;
    private readonly IHoverPresenter _popups;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<(string Root, string Path)?> _document;

    private CancellationTokenSource? _pending;
    private FilePositionHit? _asking;
    private FilePositionHit? _showing;
    private PointF _anchor;
    private readonly Func<TimeSpan, CancellationToken, Task> _dwell;

    public HoverProbeController(
        IHoverSurface surface,
        IHoverSource servers,
        IHoverPresenter popups,
        IUiDispatcher dispatcher,
        Func<(string Root, string Path)?> document,
        Func<TimeSpan, CancellationToken, Task>? dwell = null)
    {
        _dwell = dwell ?? Task.Delay;
        _surface = surface;
        _servers = servers;
        _popups = popups;
        _dispatcher = dispatcher;
        _document = document;
    }

    public override void OnMouseExit(ref MouseExitEvent e) => Dismiss();

    public override void OnMouseMoved(ref MouseMoveEvent e) => PointerMovedTo(e.Mouse.Point);

    internal void PointerMovedTo(PointF point)
    {
        if (_showing is not null && OverTheCard(point)) return;

        var at = _surface.HitTestFilePosition(point);
        if (at is null)
        {
            Dismiss();
            return;
        }

        if (_asking is { } asking && asking == at.Value) return;
        if (_showing is { } shown && shown == at.Value) return;

        Cancel();
        _showing = null;
        _popups.Hide(this);

        if (_document() is not { } document) return;

        var problems = _surface.DiagnosticsOn(at.Value.Line);
        if (problems.Count == 0 && !_servers.Handles(document.Path)) return;

        Ask(document.Root, document.Path, at.Value, point, problems);
    }

    private void Ask(
        string repoRoot,
        string path,
        FilePositionHit at,
        PointF anchor,
        IReadOnlyList<Diagnostic> problems)
    {
        var cancel = new CancellationTokenSource();
        _pending = cancel;
        _asking = at;
        var token = cancel.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await _dwell(TimeSpan.FromMilliseconds(DwellMs), token).ConfigureAwait(false);
                var answer = _servers.Handles(path)
                    ? await _servers.HoverAsync(repoRoot, path, at.Line, at.Column, token).ConfigureAwait(false)
                    : null;
                var hover = HoverCardText.Compose(problems, answer);
                if (hover is null || token.IsCancellationRequested) return;

                _dispatcher.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _showing = at;
                    _anchor = anchor;
                    _popups.Show(this, hover, new RectF(anchor.X, anchor.Y, 1, 1));
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }, token);
    }

    private bool OverTheCard(PointF point) =>
        point.X >= _anchor.X - HoverPopupService.Gap &&
        point.X <= _anchor.X + HoverPopupService.CardWidth + HoverPopupService.Gap &&
        point.Y <= _anchor.Y + HoverPopupService.Gap &&
        point.Y >= _anchor.Y - HoverPopupService.CardMaxHeight - HoverPopupService.Gap;

    private void Dismiss()
    {
        Cancel();
        _showing = null;
        _asking = null;
        _popups.Hide(this);
    }

    private void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        _asking = null;
    }

    public void Dispose() => Dismiss();
}

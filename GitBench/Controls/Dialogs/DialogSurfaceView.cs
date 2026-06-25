using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

public sealed class DialogSurfaceView : ContainerView
{
    // The card pops up from a hair under full size while the scrim fades in; closing reverses both,
    // a touch quicker so dismissal feels snappy. Scrim and card opacity ride linear progress (an even
    // fade); the card's scale rides the eased progress, growing about its center into place.
    private const float EnterScale = 0.94f;
    private const float EnterDuration = 0.20f;
    private const float ExitDuration = 0.13f;

    private readonly ContainerView _overlay;
    private readonly InputSystem _input;
    private readonly IFrameTicker _ticker;

    private Tween? _transition;
    private IDisposable? _fadeSub;
    private IDisposable? _scaleSub;
    private bool _closing;

    public DialogSurfaceView(InputSystem input, IFrameTicker ticker)
    {
        _input = input;
        _ticker = ticker;
        _overlay = new ContainerView
        {
            ZIndex = 1000,
        };
    }

    public void ShowDialog(View dialog)
    {
        // Replace anything still on screen (including a previous dialog mid-close) instantly.
        ClearTransition();
        _overlay.Children.Clear();
        Children.Remove(_overlay);

        var backdrop = new RectView
        {
            BackgroundColor = 0xB0000000,
        };
        backdrop.UseController(_input, new DialogInputBlockingController());
        _overlay.Children.Add(backdrop);

        _overlay.Children.Add(new CenterView
        {
            Children =
            {
                dialog,
            }
        });

        Children.Add(_overlay);

        _closing = false;
        var transition = new Tween(_ticker, EnterDuration, Easings.EaseOutCubic, reverseDurationSeconds: ExitDuration);
        _transition = transition;

        // Subscribe fires immediately with 0 — the opening frame is painted scaled-down and
        // transparent before Play advances it, so the dialog grows in rather than flashing.
        _fadeSub = transition.LinearProgress.Subscribe(p =>
        {
            backdrop.Opacity = p;
            dialog.Opacity = p;
        });
        _scaleSub = transition.Progress.Subscribe(p =>
        {
            var scale = EnterScale + (1f - EnterScale) * p;
            dialog.ScaleX = scale;
            dialog.ScaleY = scale;
        });

        transition.Completed += OnTransitionCompleted;
        transition.Play();
    }

    public void HideDialog()
    {
        if (_transition is null)
        {
            _overlay.Children.Clear();
            Children.Remove(_overlay);
            return;
        }

        _closing = true;
        _transition.Reverse();
    }

    private void OnTransitionCompleted()
    {
        if (!_closing) return; // the open animation finished — leave the dialog up

        ClearTransition();
        _overlay.Children.Clear();
        Children.Remove(_overlay);
    }

    private void ClearTransition()
    {
        _fadeSub?.Dispose();
        _scaleSub?.Dispose();
        _fadeSub = null;
        _scaleSub = null;

        if (_transition != null)
        {
            _transition.Completed -= OnTransitionCompleted;
            _transition.Dispose();
            _transition = null;
        }

        _closing = false;
    }
}

internal sealed class DialogInputBlockingController : KeyboardMouseController
{
    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        e.Consume();
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        e.Consume();
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        e.Consume();
    }
}

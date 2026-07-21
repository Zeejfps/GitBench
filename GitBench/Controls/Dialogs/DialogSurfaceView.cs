using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
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

    private readonly InputSystem _input;
    private readonly IFrameTicker _ticker;

    // Bottom-to-top stack of live dialogs. A single open dialog is one layer; an operation error
    // stacked over it (so the user can read the failure and return to retry) is a second layer.
    private readonly List<Layer> _layers = new();

    public DialogSurfaceView(InputSystem input, IFrameTicker ticker)
    {
        _input = input;
        _ticker = ticker;
    }

    /// <summary>True while any dialog is on screen — the cue for whether a new one replaces or stacks.</summary>
    public bool IsShowing => _layers.Count > 0;

    /// <summary>Replaces the whole stack with a single dialog: the standard "open a dialog" path.</summary>
    public void ShowDialog(View dialog)
    {
        while (_layers.Count > 0) RemoveLayerInstant(_layers[^1]);
        AddLayer(dialog, suspendBelow: false);
    }

    /// <summary>
    /// Stacks a dialog on top of the current one, leaving it mounted underneath. The layer beneath
    /// is suspended — its keyboard focus is blurred so typing can't leak into a now-hidden field —
    /// and restored when this layer pops.
    /// </summary>
    public void PushDialog(View dialog)
    {
        AddLayer(dialog, suspendBelow: _layers.Count > 0);
    }

    /// <summary>Pops the topmost dialog, revealing (and re-focusing) the one beneath.</summary>
    public void HideDialog()
    {
        if (_layers.Count == 0) return;
        var top = _layers[^1];
        if (top.Transition is null)
        {
            RemoveLayerInstant(top);
            return;
        }
        top.Closing = true;
        top.Transition.Reverse();
    }

    private void AddLayer(View dialog, bool suspendBelow)
    {
        // Owning the keyboard is what actually blocks the layer beneath: the backdrop stops the mouse
        // but keyboard input routes to the single global focused component regardless of z-order, so a
        // still-focused field underneath would keep swallowing keystrokes. Blur it now; restore on pop.
        IKeyboardMouseController? savedFocus = null;
        if (suspendBelow)
        {
            savedFocus = _input.FocusedComponent;
            if (savedFocus != null) _input.Blur(savedFocus);
        }

        var overlay = new ContainerView
        {
            ZIndex = 1000 + _layers.Count,
        };

        var backdrop = new RectView
        {
            BackgroundColor = 0xB0000000,
        };
        backdrop.UseController(_input, new DialogInputBlockingController());
        overlay.Children.Add(backdrop);
        overlay.Children.Add(new CenterView { Children = { dialog } });

        Children.Add(overlay);

        var layer = new Layer
        {
            Overlay = overlay,
            SavedFocus = savedFocus,
        };
        _layers.Add(layer);

        var transition = new Tween(_ticker, EnterDuration, Easings.EaseOutCubic, reverseDurationSeconds: ExitDuration);
        layer.Transition = transition;

        // Subscribe fires immediately with 0 — the opening frame is painted scaled-down and
        // transparent before Play advances it, so the dialog grows in rather than flashing.
        layer.FadeSub = transition.LinearProgress.Subscribe(p =>
        {
            backdrop.Opacity = p;
            dialog.Opacity = p;
        });
        layer.ScaleSub = transition.Progress.Subscribe(p =>
        {
            var scale = EnterScale + (1f - EnterScale) * p;
            dialog.ScaleX = scale;
            dialog.ScaleY = scale;
        });

        layer.CompletedHandler = () => OnTransitionCompleted(layer);
        transition.Completed += layer.CompletedHandler;
        transition.Play();
    }

    private void OnTransitionCompleted(Layer layer)
    {
        if (!layer.Closing) return; // the open animation finished — leave the dialog up
        RemoveLayerInstant(layer);
    }

    private void RemoveLayerInstant(Layer layer)
    {
        layer.Dispose();
        Children.Remove(layer.Overlay);
        _layers.Remove(layer);

        // Hand the keyboard back to the field this layer suspended, so the user lands back in the
        // dialog beneath ready to type. A bare StealFocus would re-focus a text input without
        // restarting its edit session, so route text inputs through BeginEditing.
        if (layer.SavedFocus is { } saved)
        {
            if (saved is BaseTextInputKbmController textInput) textInput.BeginEditing();
            else _input.StealFocus(saved);
        }
    }

    private sealed class Layer
    {
        public ContainerView Overlay = null!;
        public Tween? Transition;
        public IDisposable? FadeSub;
        public IDisposable? ScaleSub;
        public Action? CompletedHandler;
        public IKeyboardMouseController? SavedFocus;
        public bool Closing;

        public void Dispose()
        {
            FadeSub?.Dispose();
            ScaleSub?.Dispose();
            FadeSub = null;
            ScaleSub = null;

            if (Transition != null)
            {
                if (CompletedHandler != null) Transition.Completed -= CompletedHandler;
                Transition.Dispose();
                Transition = null;
            }
            CompletedHandler = null;
        }
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

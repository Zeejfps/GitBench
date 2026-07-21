using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The reusable shell for a Cancel + action dialog. Owns everything that was previously
/// hand-assembled in each dialog — the title frame, the error row, the Cancel and action
/// buttons, the right-aligned footer, and the keyboard wiring — so a dialog only supplies its
/// body fields and their bindings. This is what keeps every dialog uniform: there is one place
/// that builds the shell, so width, footer layout, and button roles can't drift per dialog.
///
/// Usage (object-initializer style):
/// <code>
/// _shell = new DialogShell(ctx, "Create branch", onClose)
/// {
///     Action = ("Create", DialogButtonRole.Primary),
///     Body = { _nameField, _startPointField },
/// };
/// AddChildToSelf(_shell.View);
/// _shell.SubmitFrom(_nameField.Input);     // Enter submits, Esc cancels, first field focused
/// // ...later, in Bind(vm):
/// _shell.BindCommand(vm.Create);           // wires action + cancel + error in one call
/// </code>
/// </summary>
internal sealed class DialogShell
{
    /// <summary>
    /// Label + role (+ optional inline click) for the action button. Implicitly constructs from
    /// a tuple so call sites read <c>Action = ("Create", DialogButtonRole.Primary)</c>, or
    /// <c>Action = ("Drop", DialogButtonRole.Destructive, () =&gt; TryDrop())</c> for the few
    /// dialogs that run their action inline instead of through an <see cref="AsyncCommand"/>.
    /// </summary>
    public readonly record struct ActionSpec(string Label, DialogButtonRole Role, Action? OnClick = null)
    {
        public static implicit operator ActionSpec((string Label, DialogButtonRole Role) t) =>
            new(t.Label, t.Role);

        public static implicit operator ActionSpec((string Label, DialogButtonRole Role, Action OnClick) t) =>
            new(t.Label, t.Role, t.OnClick);
    }

    private readonly Context _ctx;
    private readonly string _title;
    private readonly Action _onClose;

    private View? _view;
    private TextView? _errorView;
    private CheckoutDialogKbmController? _firstInputController;

    // Live state the action/cancel buttons bind to, so the label/busy/enabled can be driven after
    // the shell is built (BindCommand, SetActionLabel) without holding the button views themselves.
    private readonly State<string?> _actionLabel = new(null);
    private readonly State<bool> _actionBusy = new(false);
    private readonly State<bool> _actionEnabled = new(true);
    private readonly State<bool> _cancelEnabled = new(true);
    private Action? _action;
    private SpinnerAnimation? _spinner;
    private IMessageBus? _bus;

    /// <summary>Dialog width; one of the <see cref="DialogFrame"/> width tokens.</summary>
    public float Width { get; init; } = DialogFrame.WidthStandard;

    /// <summary>Vertical gap between body rows (and the footer).</summary>
    public float BodyGap { get; init; } = 12f;

    /// <summary>Label of the secondary (left) button. Defaults to the localized "Cancel".</summary>
    public string? CancelLabel { get; init; }

    /// <summary>The action (right) button. Required.</summary>
    public ActionSpec Action { get; init; }

    /// <summary>
    /// Optional content placed at the left of the footer in place of the empty spacer — used by
    /// the merge/rebase dialogs to show a preview chip beside the buttons.
    /// </summary>
    public View? FooterLead { get; init; }

    /// <summary>Body rows, top to bottom. Populated via a collection initializer.</summary>
    public List<View> Body { get; } = new();

    public DialogShell(Context ctx, string title, Action onClose)
    {
        _ctx = ctx;
        _title = title;
        _onClose = onClose;
    }

    /// <summary>The assembled dialog frame. Accessing it materializes the shell.</summary>
    public View View { get { EnsureBuilt(); return _view!; } }

    /// <summary>The error row, shown empty until bound.</summary>
    public TextView Error { get { EnsureBuilt(); return _errorView!; } }

    /// <summary>Runs the action button's action if enabled — for an Enter key binding.</summary>
    public void PerformAction()
    {
        EnsureBuilt();
        if (_actionEnabled.Value) _action?.Invoke();
    }

    /// <summary>Live override of the action button's label.</summary>
    public void SetActionLabel(string label)
    {
        EnsureBuilt();
        _actionLabel.Value = label;
    }

    private void EnsureBuilt()
    {
        if (_view != null) return;
        if (string.IsNullOrEmpty(Action.Label))
            throw new InvalidOperationException("DialogShell.Action must be set before use.");

        _actionLabel.Value = Action.Label;
        _action = Action.OnClick;
        _spinner = _ctx.Get<SpinnerAnimation>();
        _bus = _ctx.Get<IMessageBus>();

        _errorView = DialogFrame.ErrorView(_ctx);

        var cancelView = new SecondaryDialogButton
        {
            Label = CancelLabel ?? _ctx.Localization().Strings.Value.CommonCancel,
            Command = new Command(_onClose, _cancelEnabled),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>().BuildView(_ctx);

        var actionView = new ActionDialogButton
        {
            Label = _actionLabel,
            Role = Action.Role,
            Icon = Prop.Bind<string?>(() => _actionBusy.Value ? LucideIcons.Loader : null),
            IconRotation = _spinner != null ? Prop.Bind(_spinner.Rotation) : default,
            Command = new Command(() => _action?.Invoke(), _actionEnabled),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>().BuildView(_ctx);

        var footer = FooterLead is null
            ? DialogFrame.ButtonsRow(cancelView, actionView)
            : DialogFrame.ButtonsRow(FooterLead, cancelView, actionView);

        var content = new FlexColumnView
        {
            Gap = BodyGap,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
        foreach (var child in Body) content.Children.Add(child);
        content.Children.Add(_errorView);

        _view = DialogFrame.Build(_ctx, _title, _onClose, content, footer, Width);
    }

    /// <summary>
    /// Wires the standard async-command trio in one call: the action button shows a busy spinner
    /// and disables via the command's CanExecute, and Cancel locks out while it runs. A failure
    /// is routed to the dedicated operation-error dialog (see <see cref="RouteFailuresToErrorDialog"/>),
    /// not the inline row.
    /// </summary>
    public void BindCommand(AsyncCommand command) => BindCommand(command, null);

    /// <summary>
    /// Variant for the few dialogs that surface a load- or validation-time message inline (e.g.
    /// "no remotes configured") — that message rides <paramref name="inlineError"/> in the error
    /// row, while the command's own action failures still go to the operation-error dialog.
    /// </summary>
    public void BindCommand(AsyncCommand command, IReadable<string?>? inlineError)
    {
        EnsureBuilt();
        _action = command.Execute;
        _actionEnabled.BindTo(command.CanExecute);
        _cancelEnabled.BindTo(new Derived<bool>(() => !command.IsRunning.Value));
        command.IsRunning.Subscribe(running =>
        {
            _actionBusy.Value = running;
            if (running) _spinner?.Start();
            else _spinner?.Stop();
        });

        if (inlineError != null)
            Error.BindText(inlineError, s => s ?? string.Empty);

        RouteFailuresToErrorDialog(command);
    }

    /// <summary>
    /// When the action fails, hand the failure to the dedicated operation-error dialog — the full
    /// scrollable error, a copy button, and stale-lock recovery — stacked over this dialog so the
    /// user can read it and return to retry, instead of a truncated line squeezed into the frame.
    /// </summary>
    private void RouteFailuresToErrorDialog(AsyncCommand command)
    {
        command.Error.Subscribe(error =>
        {
            if (!string.IsNullOrEmpty(error))
                _bus?.Broadcast(new ShowOperationErrorMessage(_title, error));
        });
    }

    /// <summary>
    /// Attaches the text-input keyboard controller (Enter performs the action, Esc cancels) to
    /// each input, in order. The first input becomes the one <see cref="BeginEditing"/> focuses, and
    /// Tab / Shift+Tab cycle focus between them through a shared <see cref="FocusRing"/> (wrapping at
    /// the ends). Controllers go on the inputs rather than the dialog because the text-input controller
    /// consumes left-press inside its view, which would otherwise swallow clicks meant for the buttons.
    /// </summary>
    public void SubmitFrom(params TextInputView[] inputs)
    {
        EnsureBuilt();
        var inputSystem = _ctx.Require<InputSystem>();
        var clipboard = _ctx.Get<IClipboard>();
        var ring = new FocusRing();
        foreach (var input in inputs)
        {
            var controller = new CheckoutDialogKbmController(
                input, inputSystem, clipboard, PerformAction, _onClose);
            var stop = ring.Add(controller.BeginEditing, controller.EndEditing);
            controller.OnTab = () => ring.Next(stop);
            controller.OnShiftTab = () => ring.Previous(stop);
            input.UseController(inputSystem, controller);
            _firstInputController ??= controller;
        }
    }

    /// <summary>Begins editing the first input registered via <see cref="SubmitFrom"/>.</summary>
    public void BeginEditing() => _firstInputController?.BeginEditing();
}

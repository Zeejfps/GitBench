using GitBench.Features.Branches;
using GitBench.Infrastructure;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
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
    private DialogButton? _actionButton;
    private DialogButton? _cancelButton;
    private TextView? _errorView;
    private CheckoutDialogKbmController? _firstInputController;

    /// <summary>Dialog width; one of the <see cref="DialogFrame"/> width tokens.</summary>
    public float Width { get; init; } = DialogFrame.WidthStandard;

    /// <summary>Vertical gap between body rows (and the footer).</summary>
    public float BodyGap { get; init; } = 12f;

    /// <summary>Label of the secondary (left) button. Defaults to "Cancel".</summary>
    public string CancelLabel { get; init; } = "Cancel";

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

    /// <summary>The action (right) button, e.g. for a custom keyboard binding.</summary>
    public DialogButton ActionButton { get { EnsureBuilt(); return _actionButton!; } }

    /// <summary>The Cancel (left) button.</summary>
    public DialogButton CancelButton { get { EnsureBuilt(); return _cancelButton!; } }

    /// <summary>The error row, shown empty until bound.</summary>
    public TextView Error { get { EnsureBuilt(); return _errorView!; } }

    private void EnsureBuilt()
    {
        if (_view != null) return;
        if (string.IsNullOrEmpty(Action.Label))
            throw new InvalidOperationException("DialogShell.Action must be set before use.");

        _errorView = DialogFrame.ErrorView(_ctx);
        _cancelButton = new DialogButton(_ctx, CancelLabel, _onClose) { Height = DialogFrame.DefaultButtonHeight };
        _actionButton = new DialogButton(_ctx, Action.Label, Action.OnClick, Action.Role) { Height = DialogFrame.DefaultButtonHeight };

        var footer = FooterLead is null
            ? DialogFrame.ButtonsRow(_cancelButton, _actionButton)
            : DialogFrame.ButtonsRow(FooterLead, _cancelButton, _actionButton);

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
    /// and disables via the command's CanExecute, Cancel locks out while it runs, and the error
    /// row mirrors the command's error.
    /// </summary>
    public void BindCommand(AsyncCommand command) => BindCommand(command, command.Error);

    /// <summary>
    /// Variant for view models that surface their error on a property separate from the
    /// command's own <see cref="AsyncCommand.Error"/> (the busy/disable wiring is identical).
    /// </summary>
    public void BindCommand(AsyncCommand command, IReadable<string?> error)
    {
        ActionButton.BindBusyCommand(command);
        CancelButton.DisableWhile(command.IsRunning);
        Error.BindText(error, s => s ?? string.Empty);
    }

    /// <summary>
    /// Attaches the text-input keyboard controller (Enter performs the action, Esc cancels) to
    /// each input. The first input becomes the one <see cref="BeginEditing"/> focuses. Controllers
    /// go on the inputs rather than the dialog because the text-input controller consumes
    /// left-press inside its view, which would otherwise swallow clicks meant for the buttons.
    /// </summary>
    public void SubmitFrom(params TextInputView[] inputs)
    {
        EnsureBuilt();
        var inputSystem = _ctx.Require<InputSystem>();
        var clipboard = _ctx.Get<IClipboard>();
        foreach (var input in inputs)
        {
            var controller = new CheckoutDialogKbmController(
                input, inputSystem, clipboard, () => _actionButton!.PerformClick(), _onClose);
            input.UseController(inputSystem, controller);
            _firstInputController ??= controller;
        }
    }

    /// <summary>Begins editing the first input registered via <see cref="SubmitFrom"/>.</summary>
    public void BeginEditing() => _firstInputController?.BeginEditing();
}

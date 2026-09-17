using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// The standard Cancel + action modal: title frame, error row, Cancel and action buttons,
/// right-aligned footer, keyboard wiring. Supply the body as widgets; wire the action through
/// <see cref="Command"/> for the busy/disable/error trio, or give the action an inline
/// OnClick for synchronous dialogs.
/// </summary>
internal sealed record Dialog : Widget
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

    public required string Title { get; init; }
    public required Action OnClose { get; init; }
    public required ActionSpec Action { get; init; }
    public IWidget[] Body { get; init; } = [];
    public float BodyGap { get; init; } = 12f;
    /// <summary>Disable for a short form hosted in a window sized to fit all its controls.</summary>
    public bool ScrollBody { get; init; } = true;
    public string? CancelLabel { get; init; }

    /// <summary>
    /// Optional content placed at the left of the footer in place of the empty spacer — used by
    /// the merge/rebase dialogs to show a preview chip beside the buttons.
    /// </summary>
    public IWidget? FooterLead { get; init; }

    /// <summary>
    /// Optional in-place fix offered in the operation-error dialog when <see cref="Command"/> fails
    /// (e.g. unlock a locked worktree). Given the failure text, returns a recovery or null.
    /// </summary>
    public Func<string, OperationErrorRecovery?>? ErrorRecovery { get; init; }

    /// <summary>Busy spinner + disable + error row follow this command while it runs.</summary>
    public AsyncCommand? Command { get; init; }

    /// <summary>
    /// Inline load- or validation-time message shown in the error row (e.g. "no remotes
    /// configured"). Action failures are not routed here — they surface in the operation-error
    /// dialog — so this is only for messages a dialog wants visible before/independent of its action.
    /// </summary>
    public IReadable<string?>? InlineError { get; init; }

    /// <summary>Live override of the action button's label.</summary>
    public IReadable<string>? BindActionLabel { get; init; }

    /// <summary>Enter performs the action, Esc cancels — for input-free confirmation dialogs.</summary>
    public bool ConfirmKeys { get; init; }

    /// <summary>Validation gate for a synchronous action. Async actions use Command.CanExecute.</summary>
    public IReadable<bool>? ActionEnabled { get; init; }

    protected override View CreateView(Context ctx)
    {
        var actionLabel = new State<string?>(Action.Label);
        var actionBusy = new State<bool>(false);
        var actionEnabled = new State<bool>(true);
        var cancelEnabled = new State<bool>(true);
        var action = Command != null ? Command.Execute : Action.OnClick;
        var spinner = ctx.Get<SpinnerAnimation>();

        void PerformAction()
        {
            if (actionEnabled.Value) action?.Invoke();
        }

        var cancelView = new SecondaryDialogButton
        {
            Label = CancelLabel ?? ctx.Localization().Strings.Value.CommonCancel,
            Command = new Command(OnClose, cancelEnabled),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>().BuildView(ctx);

        var actionView = new ActionDialogButton
        {
            Label = actionLabel,
            Role = Action.Role,
            Icon = Prop.Bind<string?>(() => actionBusy.Value ? LucideIcons.Loader : null),
            IconRotation = spinner != null ? Prop.Bind(spinner.Rotation) : default,
            Command = new Command(() => action?.Invoke(), actionEnabled),
            Height = DialogFrame.DefaultButtonHeight,
        }.WithController<KbmController>().BuildView(ctx);

        var footer = FooterLead is null
            ? DialogFrame.ButtonsRow(cancelView, actionView)
            : DialogFrame.ButtonsRow(FooterLead.BuildView(ctx), cancelView, actionView);

        // Body widgets build against a child scope carrying the input registry, so input
        // widgets can opt in to the dialog's submit/focus wiring no matter how deeply nested.
        var inputs = new DialogInputRegistry();
        var bodyScope = new Context(ctx);
        bodyScope.AddService(inputs);
        var content = new FlexColumnView
        {
            Gap = BodyGap,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
        foreach (var widget in Body)
            content.Children.Add(widget.BuildView(bodyScope));
        var errorView = DialogFrame.ErrorView(ctx);
        errorView.IsVisible = false;
        if (InlineError is not null)
            errorView.Bind(InlineError, error =>
            {
                errorView.Text = error ?? string.Empty;
                errorView.IsVisible = !string.IsNullOrEmpty(error);
            });
        content.Children.Add(errorView);

        var width = Width.IsSet ? Width.Value : DialogFrame.WidthStandard;
        var frame = DialogFrame.Build(ctx, Title, OnClose, content, footer, width, scrollBody: ScrollBody);

        // Stacked under a clip that keeps over-wide body content (e.g. unbreakable paths)
        // from drawing past the frame's rounded edge; the shadow sits outside the clip so
        // its blur isn't cut off.
        var shadow = new RectView { BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius) };
        shadow.BindThemed(ctx.Theme(), s => shadow.BoxShadow = new BoxShadowStyle
        {
            OffsetX = 0f,
            OffsetY = -8f,
            Blur = 24f,
            Spread = 0f,
            Color = s.DialogFrame.Shadow,
        });

        var root = new ContainerView();
        if (Command is null && ActionEnabled is { } enabled)
            root.Bind(enabled, value => actionEnabled.Value = value);
        root.Children.Add(shadow);
        root.Children.Add(new ClippingView { Children = { frame } });

        if (Command is { } command)
        {
            actionEnabled.BindTo(command.CanExecute);
            cancelEnabled.BindTo(new Derived<bool>(() => !command.IsRunning.Value));
            command.IsRunning.Subscribe(running =>
            {
                actionBusy.Value = running;
                if (running) spinner?.Start();
                else spinner?.Stop();
            });

            // A failure goes to the dedicated operation-error dialog — the full scrollable error, a
            // copy button, and stale-lock recovery — stacked over this dialog so the user can read
            // it and return to retry, instead of a truncated line squeezed into the frame.
            var bus = ctx.Get<IMessageBus>();
            command.Error.Subscribe(error =>
            {
                if (!string.IsNullOrEmpty(error))
                    bus?.Broadcast(new ShowOperationErrorMessage(Title, error, ErrorRecovery?.Invoke(error)));
            });
        }

        // Enter performs the action and Esc cancels from any registered input, and Tab / Shift+Tab
        // cycle focus between them through a shared FocusRing (wrapping at the ends). Controllers go
        // on the inputs rather than the dialog because the text-input controller consumes left-press
        // inside its view, which would otherwise swallow clicks meant for the buttons.
        if (inputs.Entries.Count > 0)
        {
            var inputSystem = ctx.Require<InputSystem>();
            var clipboard = ctx.Require<IClipboard>();
            var ring = new FocusRing();
            var controllers = new List<DialogTextInputKbmController>(inputs.Entries.Count);
            foreach (var entry in inputs.Entries)
            {
                var controller = new DialogTextInputKbmController(
                    entry.Input, inputSystem, clipboard, PerformAction, OnClose);
                var stop = ring.Add(controller.BeginEditing, controller.EndEditing);
                controller.OnTab = () => ring.Next(stop);
                controller.OnShiftTab = () => ring.Previous(stop);
                entry.Input.UseController(inputSystem, controller);
                controllers.Add(controller);
            }
            root.Behaviors.Add(new MountAction(() =>
            {
                if (inputs.Entries[0].SelectAllOnOpen) inputs.Entries[0].Input.SelectAll();
                controllers[0].BeginEditing();
            }));
        }

        if (ConfirmKeys)
        {
            var dialogState = new DialogState(OnClose, PerformAction);
            root.UseController(ctx.Require<InputSystem>(),
                () => new DialogKbmController(dialogState));
        }

        if (BindActionLabel != null)
            root.Bind(BindActionLabel, label => actionLabel.Value = label);

        return root;
    }

    private sealed class MountAction : IViewBehavior
    {
        private readonly Action _onMount;

        public MountAction(Action onMount)
        {
            _onMount = onMount;
        }

        public void Attach(View view) => _onMount();

        public void Detach(View view) { }
    }
}

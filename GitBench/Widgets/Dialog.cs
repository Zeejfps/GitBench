using GitBench.Controls.Dialogs;
using GitBench.Infrastructure;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// The standard Cancel + action modal, as a widget. Wraps <see cref="DialogShell"/> (one
/// source of truth for frame, footer, button roles) so widget dialogs and legacy view
/// dialogs render identically. Supply the body as widgets; wire the action through
/// <see cref="Command"/> for the busy/disable/error trio, or give the action an inline
/// OnClick for synchronous dialogs.
/// </summary>
internal sealed record Dialog : Widget
{
    public required string Title { get; init; }
    public required Action OnClose { get; init; }
    public required DialogShell.ActionSpec Action { get; init; }
    public IWidget[] Body { get; init; } = [];
    public float BodyGap { get; init; } = 12f;
    public string CancelLabel { get; init; } = "Cancel";
    public IWidget? FooterLead { get; init; }

    /// <summary>Busy spinner + disable + error row follow this command while it runs.</summary>
    public AsyncCommand? Command { get; init; }

    /// <summary>Error source when the VM surfaces it separately from the command's own.</summary>
    public IReadable<string?>? Error { get; init; }

    /// <summary>Enter performs the action, Esc cancels — for input-free confirmation dialogs.</summary>
    public bool ConfirmKeys { get; init; }

    protected override View CreateView(Context ctx)
    {
        var shell = new DialogShell(ctx, Title, OnClose)
        {
            Action = Action,
            BodyGap = BodyGap,
            CancelLabel = CancelLabel,
            Width = Width.IsSet ? Width.Value : DialogFrame.WidthStandard,
            FooterLead = FooterLead?.BuildView(ctx),
        };

        // Body widgets build against a child scope carrying the input registry, so input
        // widgets can opt in to the dialog's submit/focus wiring no matter how deeply nested.
        var inputs = new DialogInputRegistry();
        var bodyScope = new Context(ctx);
        bodyScope.AddService(inputs);
        foreach (var widget in Body)
            shell.Body.Add(widget.BuildView(bodyScope));

        var root = new ContainerView();
        root.Children.Add(shell.View);

        if (Command != null)
        {
            if (Error != null) shell.BindCommand(Command, Error);
            else shell.BindCommand(Command);
        }

        if (inputs.Entries.Count > 0)
        {
            shell.SubmitFrom(inputs.Entries.Select(e => e.Input).ToArray());
            root.Behaviors.Add(new MountAction(() =>
            {
                var first = inputs.Entries[0];
                if (first.SelectAllOnOpen) first.Input.SelectAll();
                shell.BeginEditing();
            }));
        }

        if (ConfirmKeys)
        {
            root.UseController(ctx.Require<InputSystem>(),
                () => new DialogKbmController(() => shell.ActionButton.PerformClick(), OnClose));
        }

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

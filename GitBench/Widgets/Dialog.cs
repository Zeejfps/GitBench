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
        var shell = new DialogShell(Title, OnClose)
        {
            Action = Action,
            CancelLabel = CancelLabel,
            Width = Width.IsSet ? Width.Value : DialogFrame.WidthStandard,
            FooterLead = FooterLead?.BuildView(ctx),
        };
        foreach (var widget in Body)
            shell.Body.Add(widget.BuildView(ctx));

        var root = new ContainerView();
        root.Children.Add(shell.View);

        if (Command != null)
        {
            if (Error != null) shell.BindCommand(Command, Error);
            else shell.BindCommand(Command);
        }

        if (ConfirmKeys)
        {
            root.UseController(ctx.Require<InputSystem>(),
                () => new DialogKbmController(() => shell.ActionButton.PerformClick(), OnClose));
        }

        return root;
    }
}

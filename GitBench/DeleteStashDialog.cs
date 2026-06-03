using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal shown when the user picks "Delete…" on a stash row. Running
/// `git stash drop` is destructive — the stash cannot be recovered from the UI
/// afterwards, so the action is gated behind this prompt.
/// </summary>
public sealed class DeleteStashDialog : MultiChildView
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    private bool _isRunning;

    public DeleteStashDialog(Repo repo, int index, string subject, Action onClose)
    {
        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"{subject}\n\nThis stash will be permanently deleted. This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell("Delete stash?", onClose)
        {
            Width = DialogFrame.WidthCompact,
            Action = ("Delete", DialogButtonRole.Destructive, () => TryDelete(repo, index)),
            Body = { prompt },
        };
        AddChildToSelf(_shell.View);

        this.UsePresenter(ctx => new DeleteStashPresenter(
            this,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>()));
    }

    private void TryDelete(Repo repo, int index)
    {
        if (_isRunning) return;
        _isRunning = true;
        _shell.ActionButton.IsEnabled.Value = false;
        _shell.Error.Text = string.Empty;
        DeleteRequested?.Invoke(repo, index);
    }

    internal event Action<Repo, int>? DeleteRequested;

    internal void OnDeleteOutcome(StashOutcome outcome)
    {
        if (!outcome.Success)
        {
            _isRunning = false;
            _shell.ActionButton.IsEnabled.Value = true;
            _shell.Error.Text = outcome.ErrorMessage ?? "Stash drop failed.";
            return;
        }
        _onClose();
    }
}

internal sealed class DeleteStashPresenter : IDisposable
{
    private readonly DeleteStashDialog _view;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly OperationRunner _runner;

    public DeleteStashPresenter(
        DeleteStashDialog view,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _view = view;
        _gitService = gitService;
        _bus = bus;
        _runner = new OperationRunner(dispatcher);
        _view.DeleteRequested += OnDeleteRequested;
    }

    public void Dispose()
    {
        _view.DeleteRequested -= OnDeleteRequested;
    }

    private void OnDeleteRequested(Repo repo, int index)
    {
        _runner.Run(
            () => _gitService.DropStash(repo, index),
            ex => new StashOutcome(false, ex.Message),
            outcome =>
            {
                _view.OnDeleteOutcome(outcome);
                if (outcome.Success)
                    _bus.Broadcast(new RefsChangedMessage(repo.Id));
            });
    }
}

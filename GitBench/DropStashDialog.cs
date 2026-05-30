using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal shown after a stash is successfully applied. Lets the user
/// drop the stash (the natural finish of "pop") or keep it around for re-use.
/// Running `git stash drop` here is destructive — the stash cannot be recovered
/// from the UI afterwards.
/// </summary>
public sealed class DropStashDialog : MultiChildView
{
    private readonly Action _onClose;
    private readonly DialogButton _dropButton;
    private readonly TextView _errorView;

    private bool _isRunning;

    public DropStashDialog(Repo repo, int index, string label, string subject, Action onClose)
    {
        Width = 460f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Applied: {subject}\n\nDrop this stash now? This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _errorView = DialogFrame.ErrorView();

        var keepButton = new DialogButton("Keep", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _dropButton = new DialogButton("Drop", () => TryDrop(repo, index)) { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build($"Drop {label}?", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                _errorView,
                DialogFrame.ButtonsRow(keepButton, _dropButton),
            },
        }));

        // The drop call is small enough to inline here; no presenter needed. We grab
        // services from the context the dialog is attached to.
        this.UsePresenter(ctx => new DropStashPresenter(
            this,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>()));
    }

    private void TryDrop(Repo repo, int index)
    {
        if (_isRunning) return;
        _isRunning = true;
        _dropButton.IsEnabled.Value = false;
        _errorView.Text = string.Empty;
        DropRequested?.Invoke(repo, index);
    }

    internal event Action<Repo, int>? DropRequested;

    internal void OnDropOutcome(StashOutcome outcome, Repo repo)
    {
        if (!outcome.Success)
        {
            _isRunning = false;
            _dropButton.IsEnabled.Value = true;
            _errorView.Text = outcome.ErrorMessage ?? "Stash drop failed.";
            return;
        }
        _onClose();
    }
}

internal sealed class DropStashPresenter : IDisposable
{
    private readonly DropStashDialog _view;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly OperationRunner _runner;

    public DropStashPresenter(
        DropStashDialog view,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _view = view;
        _gitService = gitService;
        _bus = bus;
        _runner = new OperationRunner(dispatcher);
        _view.DropRequested += OnDropRequested;
    }

    public void Dispose()
    {
        _view.DropRequested -= OnDropRequested;
    }

    private void OnDropRequested(Repo repo, int index)
    {
        _runner.Run(
            () => _gitService.DropStash(repo, index),
            ex => new StashOutcome(false, ex.Message),
            outcome =>
            {
                _view.OnDropOutcome(outcome, repo);
                if (outcome.Success)
                    _bus.Broadcast(new RefsChangedMessage(repo.Id));
            });
    }
}

using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class DiscardHunkDialog : MultiChildView, IBind<DiscardHunkViewModel>
{
    private readonly DialogButton _discardButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly Action _onClose;

    public DiscardHunkDialog(Repo repo, string path, string patch, Action onClose)
    {
        Width = 460f;
        Height = 200f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Discard this hunk in {path}? This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _discardButton = new DialogButton("Discard") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Discard hunk", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = prompt },
                _errorView,
                DialogFrame.ButtonsRow(_cancelButton, _discardButton),
            },
        }));

        this.UseController(_ => new DialogKbmController(_discardButton.Command, _onClose));

        var request = new DiscardHunkRequest(repo, patch);
        this.UseViewModel(
            ctx => new DiscardHunkViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DiscardHunkViewModel vm)
    {
        _discardButton.BindBusyCommand(vm.Discard);
        _cancelButton.DisableWhile(vm.Discard.IsRunning);
        _errorView.BindText(vm.Discard.Error, s => s ?? string.Empty);
        vm.CloseRequested += _onClose;
    }
}

public readonly record struct DiscardHunkRequest(Repo Repo, string Patch);

internal sealed class DiscardHunkViewModel : IDisposable
{
    public AsyncCommand Discard { get; }
    public event Action? CloseRequested;

    public DiscardHunkViewModel(
        DiscardHunkRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Discard = new AsyncCommand(
            dispatcher,
            work: () => gitService.ApplyPatch(request.Repo, request.Patch, cached: false, reverse: true),
            onSuccess: () =>
            {
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}

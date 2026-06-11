using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed class DiscardHunkDialog : MultiChildView, IBind<DiscardHunkViewModel>
{
    private readonly DialogShell _shell;
    private readonly Action _onClose;

    public DiscardHunkDialog(Repo repo, string path, string patch, Action onClose)
    {
        Height = 200f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Discard this hunk in {path}? This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell("Discard hunk", onClose)
        {
            Width = DialogFrame.WidthCompact,
            Action = ("Discard", DialogButtonRole.Destructive),
            Body = { new FlexItem { Grow = 1, Child = prompt } },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

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
        _shell.BindCommand(vm.Discard);
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
        Discard = AsyncCommand.ForOutcome(
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

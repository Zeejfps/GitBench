using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed record DiscardHunkDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string Path { get; init; }
    public required string Patch { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DiscardHunkViewModel(
            new DiscardHunkRequest(Repo, Patch),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = s.LocalchangesDiscardHunkTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            Height = 200f,
            Action = (s.CommonDiscard, DialogButtonRole.Destructive),
            Command = vm.Discard,
            ConfirmKeys = true,
            Body =
            [
                new Grow
                {
                    Child = new Text
                    {
                        Value = s.LocalchangesDiscardHunkBody(Path),
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
            ],
        };
    }
}

public readonly record struct DiscardHunkRequest(Repo Repo, string Patch);

internal sealed class DiscardHunkViewModel : IDialogViewModel
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

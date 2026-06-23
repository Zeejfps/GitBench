using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Stash;

/// <summary>
/// Confirmation modal shown after a stash is successfully applied. Lets the user
/// drop the stash (the natural finish of "pop") or keep it around for re-use.
/// Running `git stash drop` here is destructive — the stash cannot be recovered
/// from the UI afterwards.
/// </summary>
internal sealed record DropStashDialog : Widget
{
    public required Repo Repo { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }
    public required string Subject { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DropStashViewModel(
            Repo,
            Index,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashDropTitle(Label),
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthCompact,
            CancelLabel = s.StashDropCancel,
            Action = (s.StashDropAction, DialogButtonRole.Destructive),
            Command = vm.Drop,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.StashDropBody(Subject),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }
}

using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Stash;

/// <summary>
/// Modal shown when the user picks "Rename…" on a stash row. Edits the stash's
/// description. git has no native stash rename, so <see cref="IGitStashOperations.RenameStash"/>
/// drops the entry and re-stores it under the new message — which moves the renamed
/// stash to the top of the list (stash@{0}).
/// </summary>
internal sealed record RenameStashDialog : Widget
{
    public required Repo Repo { get; init; }
    public required int Index { get; init; }
    public required string CurrentMessage { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var index = Index;
        var oldMessage = CurrentMessage;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitStashOperations>();
        var bus = ctx.Require<IMessageBus>();

        var message = new State<string>(oldMessage);
        var gate = new Derived<bool>(() => message.Value.Length > 0 && message.Value != oldMessage);
        var rename = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.RenameStash(repo, index, message.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            },
            gate: gate);

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashRenameTitle,
            OnClose = onClose,
            Action = (s.CommonRename, DialogButtonRole.Primary),
            Command = rename,
            Body =
            [
                new Text
                {
                    Value = s.StashRenameContext(CurrentMessage),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = s.StashRenameDescriptionLabel,
                    Value = message,
                    SelectAllOnOpen = true,
                },
            ],
        };
    }
}

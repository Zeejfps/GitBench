using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Stash;

/// <summary>
/// Modal shown when the user picks "Rename…" on a stash row. Edits the stash's
/// description. git has no native stash rename, so <see cref="IGitService.RenameStash"/>
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
        var vm = new RenameStashDialogViewModel(
            new RenameStashRequest(Repo, Index, CurrentMessage),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Rename stash",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Rename", DialogButtonRole.Primary),
            Command = vm.Rename,
            Body =
            [
                new Text
                {
                    Value = $"Renaming '{CurrentMessage}'",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = "Description",
                    Value = vm.Message,
                    SelectAllOnOpen = true,
                },
            ],
        };
    }
}

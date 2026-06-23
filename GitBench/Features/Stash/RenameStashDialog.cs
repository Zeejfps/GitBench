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

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashRenameTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonRename, DialogButtonRole.Primary),
            Command = vm.Rename,
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
                    Value = vm.Message,
                    SelectAllOnOpen = true,
                },
            ],
        };
    }
}

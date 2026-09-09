using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>Offers to stage a conflicted file after it has been saved.</summary>
internal sealed record MarkResolvedDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string RelativePath { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new MarkResolvedDialogViewModel(
            Repo,
            RelativePath,
            ctx.Require<IGitConflictOperations>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.EditorMarkResolvedTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.LocalchangesConflictMarkResolved, DialogButtonRole.Primary),
            Command = vm.MarkResolved,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.EditorMarkResolvedBody(RelativePath),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.EditorMarkResolvedHint,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}

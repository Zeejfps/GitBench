using GitBench.Controls.Dialogs;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Observable;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Stash;

// Modal shown when the user clicks Stash in the actions toolbar. Lets the user name the
// stash, pick the files to stash, and optionally keep the index (--keep-index) so staged
// hunks stay around after stashing. --include-untracked is derived from the row checks:
// passed iff any selected row is an untracked file.
internal sealed record StashDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var snapshot = LocalChangesProjection.ActiveSnapshot(ctx.Require<IRepoSnapshotStore>(), Repo);
        var vm = new StashDialogViewModel(
            Repo,
            snapshot,
            ctx.Require<IGitStashOperations>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<LocalChangesSelectionStore>(),
            ctx.Localization(),
            OnClose);

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Height = 520f,
            BodyGap = 10,
            Action = (s.StashAction, DialogButtonRole.Primary),
            Command = vm.Stash,
            Body =
            [
                new LabeledInput
                {
                    Label = s.CommonMessage,
                    Value = vm.Message,
                },
                new Text
                {
                    Value = Prop.Bind(vm.Files.Header),
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                new Grow { Child = new DialogFileList { List = vm.Files, EmptyText = s.StashDialogNoChanges } },
                new CheckboxWidget
                {
                    Label = s.StashKeepStagedCheckbox,
                    Checked = vm.KeepStaged,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}

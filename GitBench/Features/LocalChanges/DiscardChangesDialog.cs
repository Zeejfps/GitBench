using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Observable;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Confirmation modal for discarding unstaged changes. Lists every unstaged path with a
/// checkbox — the paths the user had selected when they invoked Discard come pre-checked —
/// so they can fine-tune the set before committing to the throw-away. Discard is a
/// destructive action: the worktree changes (and any untracked files in the set) cannot
/// be recovered from git afterwards.
/// </summary>
internal sealed record DiscardChangesDialog : Widget
{
    public required Repo Repo { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var snapshot = LocalChangesProjection.ActiveSnapshot(ctx.Require<IRepoSnapshotStore>(), Repo);
        var vm = new DiscardChangesViewModel(
            Repo,
            Paths,
            snapshot,
            ctx.Require<IGitWorkingTreeOperations>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization(),
            OnClose);

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.LocalchangesDiscardDialogTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Height = 480f,
            BodyGap = 10,
            Action = (s.CommonDiscard, DialogButtonRole.Destructive),
            Command = vm.Discard,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.LocalchangesDiscardDialogBody },
                new Text
                {
                    Value = Prop.Bind(vm.Files.Header),
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                new Grow { Child = new DialogFileList { List = vm.Files, EmptyText = s.LocalchangesDiscardDialogNoChanges } },
            ],
        };
    }
}

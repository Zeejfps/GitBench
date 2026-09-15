using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

/// <summary>
/// Modal shown from a primary RepoRow's "Add submodule…" menu. Collects the URL, path,
/// and optional tracked branch that `git submodule add` needs, plus a force toggle
/// for re-using a path that's been previously used.
/// </summary>
internal sealed record AddSubmoduleDialog : Widget
{
    public required Repo Primary { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var primary = Primary;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitSubmoduleOperations>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Localization();

        var url = new State<string>(string.Empty);
        var path = new State<string>(string.Empty);
        var branch = new State<string>(string.Empty);
        var force = new State<bool>(false);

        // Track branch is optional, so blank is valid; a non-blank name must be a legal refname.
        var branchStatus = new Derived<FieldStatus?>(() =>
        {
            var strings = loc.Strings.Value;
            return RefNameRules.Validate(branch.Value.Trim(), strings, strings.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() =>
            url.Value.Trim().Length > 0 && path.Value.Trim().Length > 0
            && RefNameRules.IsValid(branch.Value.Trim()));

        var add = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () =>
            {
                var trackBranch = branch.Value.Trim();
                return gitService.AddSubmodule(primary, new SubmoduleAddRequest(
                    Url: url.Value.Trim(),
                    Path: path.Value.Trim(),
                    Branch: trackBranch.Length > 0 ? trackBranch : null,
                    Force: force.Value));
            },
            onSuccess: () =>
            {
                bus.Broadcast(new SubmodulesChangedMessage(primary.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(primary.Id));
                onClose();
            },
            gate: gate);

        var s = loc.Strings.Value;
        return new Dialog
        {
            Title = s.SubmodulesAddTitle,
            OnClose = onClose,
            Action = (s.CommonAdd, DialogButtonRole.Primary),
            Command = add,
            Body =
            [
                new LabeledInput
                {
                    Label = s.CommonRepositoryUrl,
                    Value = url,
                },
                new LabeledInput
                {
                    Label = s.SubmodulesAddPathLabel,
                    Value = path,
                    Hint = s.SubmodulesAddPathHint,
                },
                new LabeledInput
                {
                    Label = s.SubmodulesAddBranchLabel,
                    Value = branch,
                    Hint = s.SubmodulesAddBranchHint,
                    Status = branchStatus,
                },
                new CheckboxWidget
                {
                    Label = s.SubmodulesAddForceLabel,
                    Checked = force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}

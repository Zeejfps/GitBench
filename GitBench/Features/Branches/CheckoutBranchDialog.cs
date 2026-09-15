using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Modal shown when the user double-clicks a remote branch that has no matching local
/// branch. Lets them pick the local branch name and whether to set up tracking, then
/// runs `git checkout -b &lt;local&gt; [--track|--no-track] &lt;remote&gt;/&lt;branch&gt;`.
/// </summary>
internal sealed record CheckoutBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string RemoteName { get; init; }
    public required string RemoteBranchName { get; init; }
    public required string SuggestedLocalName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var remoteName = RemoteName;
        var remoteBranchName = RemoteBranchName;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();
        var head = ctx.Require<IRepoHeadStore>();
        var loc = ctx.Localization();

        var name = new State<string>(SuggestedLocalName);
        var track = new State<bool>(true);
        var nameStatus = new Derived<FieldStatus?>(() =>
        {
            var strings = loc.Strings.Value;
            return RefNameRules.Validate(name.Value, strings, strings.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() => name.Value.Length > 0 && RefNameRules.IsValid(name.Value));

        var checkout = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.CheckoutRemoteBranch(repo, name.Value, remoteName, remoteBranchName, track.Value),
            // Close before broadcasting: an error broadcast swaps in the error dialog, and a stale
            // Close() afterwards would dismiss that brand-new dialog instead of this one. Both paths
            // close, so the ordering holds either way.
            onSuccess: () =>
            {
                onClose();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
            },
            gate: gate,
            onError: error =>
            {
                onClose();
                bus.Broadcast(new ShowOperationErrorMessage(loc.Strings.Value.BranchesErrorCheckoutFailed, error));
            },
            // `checkout -b` lands HEAD on the new local branch, so this is a branch switch like any
            // other — declare it, or the toolbar keeps seeding the branch being left behind.
            onStart: () => head.BeginMove(repo, name.Value));

        var s = loc.Strings.Value;
        return new Dialog
        {
            Title = s.BranchesCheckoutTitle,
            OnClose = onClose,
            Action = (s.CommonCheckout, DialogButtonRole.Primary),
            Command = checkout,
            Body =
            [
                new Text
                {
                    Value = s.BranchesCheckoutDescription(RemoteName, RemoteBranchName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = s.BranchesCheckoutLocalNameLabel,
                    Value = name,
                    Status = nameStatus,
                    SelectAllOnOpen = true,
                },
                new CheckboxWidget
                {
                    Label = s.BranchesCheckoutTrackLabel,
                    Checked = track,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}

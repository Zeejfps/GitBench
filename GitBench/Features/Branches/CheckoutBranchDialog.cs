using GitBench.Controls.Dialogs;
using GitBench.Git;
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
        var vm = new CheckoutBranchDialogViewModel(
            new CheckoutRequest(Repo, RemoteName, RemoteBranchName, SuggestedLocalName),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesCheckoutTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonCheckout, DialogButtonRole.Primary),
            Command = vm.Checkout,
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
                    Value = vm.Name,
                    Status = vm.NameStatus,
                    SelectAllOnOpen = true,
                },
                new CheckboxWidget
                {
                    Label = s.BranchesCheckoutTrackLabel,
                    Checked = vm.Track,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}

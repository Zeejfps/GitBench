using GitBench.Controls.Dialogs;
using GitBench.Git;
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
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Checkout branch",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Checkout", DialogButtonRole.Primary),
            Command = vm.Checkout,
            Body =
            [
                new Text
                {
                    Value = $"Create a local branch from {RemoteName}/{RemoteBranchName}",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = "Local branch name",
                    Value = vm.Name,
                    Status = vm.NameStatus,
                    SelectAllOnOpen = true,
                },
                new CheckboxWidget
                {
                    Label = "Track this remote branch",
                    Checked = vm.Track,
                    Height = 22,
                }.WithController<KbmController>(),
            ],
        };
    }
}

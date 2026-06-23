using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record PublishBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string LocalBranch { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new PublishBranchDialogViewModel(
            new PublishBranchRequest(Repo, LocalBranch),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Publish branch",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = ("Publish", DialogButtonRole.Primary),
            Command = vm.Publish,
            Error = vm.ErrorMessage,
            ConfirmKeys = true,
            ViewModel = vm,
            Body =
            [
                new Text
                {
                    Value = "First push — choose a remote and set the upstream",
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = "Branch:", Value = BranchChip(LocalBranch) },
                new LabeledRow { Label = "To:", Value = new RemoteDropdown { Selected = vm.SelectedRemote, Remotes = vm.Remotes } },
                new CheckboxWidget
                {
                    Label = "Track this remote branch (set upstream)",
                    Checked = vm.SetUpstream,
                    Height = 24,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static IWidget BranchChip(string name) => new Row
    {
        Gap = 6,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Text
            {
                Value = name,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogFrame.TitleText),
            },
        ],
    };
}

internal readonly record struct PublishBranchRequest(Repo Repo, string LocalBranch);

internal sealed record RemoteDropdown : Widget
{
    public required State<string> Selected { get; init; }
    public required IReadable<IReadOnlyList<string>> Remotes { get; init; }

    protected override IWidget Build(Context ctx) => new DropdownWidget
    {
        Height = 30,
        Gap = 6,
        // Hover-enabled once there's at least one remote; the chevron and the menu only appear when
        // there's an actual choice (more than one).
        Enabled = new Derived<bool>(() => Remotes.Value.Count > 0),
        ShowChevron = Prop.Bind(() => Remotes.Value.Count > 1),
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Grow
            {
                Child = new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = Prop.Bind<string?>(() =>
                        string.IsNullOrEmpty(Selected.Value) ? "(no remotes)" : Selected.Value),
                    Color = Theme.Color(s => string.IsNullOrEmpty(Selected.Value)
                        ? s.DialogBody.RowTextMissing
                        : s.DialogFrame.TitleText),
                },
            },
        ],
    }.WithMenuController(rect =>
    {
        var remotes = Remotes.Value;
        if (remotes.Count <= 1) return;
        var items = new List<RepoBarContextMenu.Item>(remotes.Count);
        foreach (var remote in remotes)
        {
            var captured = remote;
            items.Add(new RepoBarContextMenu.Item(captured, () => Selected.Value = captured));
        }
        RepoBarContextMenu.Show(ctx, rect.BottomLeft, items);
    });
}

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
            ctx.Require<IMessageBus>(),
            ctx.Localization());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesPublishTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = (s.BranchesPublishAction, DialogButtonRole.Primary),
            Command = vm.Publish,
            InlineError = vm.LoadError,
            ConfirmKeys = true,
            ViewModel = vm,
            Body =
            [
                new Text
                {
                    Value = s.BranchesPublishDescription,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = s.BranchesPublishBranchLabel, Value = BranchChip(LocalBranch) },
                new LabeledRow { Label = s.BranchesPublishRemoteLabel, Value = new RemoteDropdown { Selected = vm.SelectedRemote, Remotes = vm.Remotes } },
                new CheckboxWidget
                {
                    Label = s.BranchesPublishTrackLabel,
                    Checked = vm.SetUpstream,
                    Height = 24,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static IWidget BranchChip(string name) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Default,
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

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        return new DropdownWidget
        {
            Height = 30,
            Gap = Spacing.Sm,
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
                    FontSize = FontSize.Default,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Grow
                {
                    Child = new Text
                    {
                        VAlign = TextAlignment.Center,
                        Value = Prop.Bind<string?>(() =>
                            string.IsNullOrEmpty(Selected.Value) ? s.BranchesPublishNoRemotes : Selected.Value),
                        Color = Theme.Color(t => string.IsNullOrEmpty(Selected.Value)
                            ? t.DialogBody.RowTextMissing
                            : t.DialogFrame.TitleText),
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
}

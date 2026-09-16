using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
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
        var repo = Repo;
        var localBranch = LocalBranch;
        var onClose = OnClose;
        var gitBranches = ctx.Require<IGitBranchOperations>();
        var gitRemotes = ctx.Require<IGitRemoteOperations>();
        var dispatcher = ctx.Require<IUiDispatcher>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Localization();

        var remotes = new State<IReadOnlyList<string>>(Array.Empty<string>());
        var selectedRemote = new State<string>(string.Empty);
        var setUpstream = new State<bool>(true);
        // Load-time inline message (no remotes configured). The publish failure itself surfaces
        // in the operation-error dialog, not here.
        var loadError = new State<string?>(null);
        var gate = new Derived<bool>(() => !string.IsNullOrEmpty(selectedRemote.Value));

        var publish = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => gitBranches.PublishBranch(repo, localBranch, selectedRemote.Value, localBranch, setUpstream.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            },
            gate: gate);

        Task.Run(() =>
        {
            IReadOnlyList<string> names;
            try { names = gitRemotes.GetRemoteNames(repo); }
            catch { names = Array.Empty<string>(); }

            dispatcher.Post(() =>
            {
                remotes.Value = names;
                if (names.Count == 0)
                {
                    loadError.Value = loc.Strings.Value.BranchesPublishErrorNoRemotes;
                }
                else
                {
                    loadError.Value = null;
                    selectedRemote.Value = names.FirstOrDefault(o => o == "origin") ?? names[0];
                }
            });
        });

        var s = loc.Strings.Value;
        return new Dialog
        {
            Title = s.BranchesPublishTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.BranchesPublishAction, DialogButtonRole.Primary),
            Command = publish,
            InlineError = loadError,
            ConfirmKeys = true,
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
                new LabeledRow { Label = s.BranchesPublishRemoteLabel, Value = new RemoteDropdown { Selected = selectedRemote, Remotes = remotes } },
                new CheckboxWidget
                {
                    Label = s.BranchesPublishTrackLabel,
                    Checked = setUpstream,
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

internal sealed record RemoteDropdown : Widget
{
    public required State<string> Selected { get; init; }
    public required IReadable<IReadOnlyList<string>> Remotes { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        return new DropdownWidget
        {
            Height = DialogFrame.FieldHeight,
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

using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

/// <summary>
/// Master-detail manager for the global identity profiles: a Repos-Bar-style list card on the left
/// (a "Profiles" header with an add button, then selectable rows each carrying their own delete) and
/// the shared editable field set on the right, with Save in the footer. Opened from the status-bar
/// identity menu, preselecting the repo's active profile.
/// </summary>
internal sealed record IdentityProfileManagerDialog : Widget
{
    public required Guid? InitialProfileId { get; init; }
    public required Action OnClose { get; init; }

    private const float ListWidth = 210f;

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = new IdentityProfileManagerDialogViewModel(
            InitialProfileId,
            ctx.Require<IdentityProfileService>(),
            ctx.Require<IUiDispatcher>(),
            s.IdentityManageNewName);

        var body = new Provide<IdentityProfileManagerDialogViewModel>
        {
            Value = vm,
            Child = new Row
            {
                Gap = Spacing.Lg,
                CrossAxis = CrossAxisAlignment.Start,
                Children =
                [
                    LeftPane(vm, s),
                    new Grow { Child = RightPane(vm) },
                ],
            },
        };

        var dialog = new Dialog
        {
            Title = s.IdentityManageTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = 760f,
            CancelLabel = s.IdentityManageClose,
            Action = (s.CommonSave, DialogButtonRole.Primary),
            Command = vm.Save,
            Body = [body],
        };

        // The delete confirmation is a nested popup stacked over the whole manager card (the app dialog
        // surface can't stack a second modal, so it lives inside this widget's tree). Show mounts it only
        // while a delete is armed so its entrance replays each time; a high ZIndex lifts it — and its
        // input-blocking backdrop — above the dialog for both drawing and hit-testing.
        return new Box
        {
            Children =
            [
                dialog,
                new Show
                {
                    When = vm.HasPendingDelete,
                    Then = () => new IdentityDeleteConfirmPopup { Vm = vm, S = s, ZIndex = 1000 },
                },
            ],
        };
    }

    // A self-contained card echoing the Repos Bar: a "Profiles" title with an add button pinned to
    // the header, a divider, then the selectable profile rows.
    private static IWidget LeftPane(IdentityProfileManagerDialogViewModel vm, Strings s)
    {
        var addButton = new IconButtonWidget
        {
            Icon = LucideIcons.Plus,
            IconSize = 15f,
            Width = 24,
            Height = 24,
            Command = vm.Add,
            Surface = st => Theme.Color(t => t.HeaderActionButton.Surface(st)),
            Foreground = st => Theme.Color(t => t.HeaderActionButton.Icon(st)),
        }
            .WithTooltip(L.T(t => t.IdentityManageAdd))
            .WithController<KbmController>();

        var header = new Box
        {
            Height = 36,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(t => new BorderColorStyle { Bottom = t.DialogFrame.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Xs },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            MainAxis = MainAxisAlignment.SpaceBetween,
                            Children =
                            [
                                new Text
                                {
                                    Value = s.IdentityManageListHeader,
                                    Weight = FontWeight.Bold,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(t => t.Palette.TextStrong),
                                },
                                addButton,
                            ],
                        },
                    ],
                },
            ],
        };

        var listCard = new DialogInsetCard
        {
            Width = ListWidth,
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        header,
                        new Padding
                        {
                            Amount = PaddingStyle.All(Spacing.Xs),
                            Children =
                            [
                                new Each<IdentityProfile>
                                {
                                    Items = vm.Profiles,
                                    Template = new IdentityProfileListRow(),
                                    Gap = Spacing.Hair,
                                    CrossAxis = CrossAxisAlignment.Stretch,
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        return listCard;
    }

    private static IWidget RightPane(IdentityProfileManagerDialogViewModel vm) => new IdentityProfileFields
    {
        DisplayName = vm.DisplayName,
        AuthorName = vm.AuthorName,
        AuthorEmail = vm.AuthorEmail,
        SshKeyPath = vm.SshKeyPath,
        MatchHost = vm.MatchHost,
        MatchOwner = vm.MatchOwner,
    };
}

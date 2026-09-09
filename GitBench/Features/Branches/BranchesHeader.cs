using GitBench.App;
using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// The rail's header: which branch the repository is on, and the toggle that decides which of the
/// two lists below says where you are — the branches, or the files.
/// </summary>
internal sealed record BranchesHeader : Widget
{
    private const float HeaderHeight = 44f;
    private const int HorizontalPadding = 8;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<BranchesHeaderViewModel>();
        var theme = ctx.Theme();
        var pane = ctx.Require<State<SidebarPane>>();
        var browsers = ctx.Require<IFileBrowserStore>();

        bool OnFiles() => pane.Value == SidebarPane.Files;

        return new Box
        {
            Height = HeaderHeight,
            BorderSize = new BorderSizeStyle { Top = 0, Bottom = 1 },
            Background = theme.Styles.Bind(s => s.BranchesHeader.Background),
            BorderColor = theme.Styles.Bind(s => new BorderColorStyle { Bottom = s.BranchesHeader.BorderBottom }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new BranchLabel
                                {
                                    BranchName = vm.BranchName,
                                    IsDetached = vm.IsDetached,
                                    IsSwitching = vm.IsSwitching,
                                    SwitchRotation = vm.SwitchRotation,
                                },
                                new Spacer(),
                                new LocalChangesHeaderActionButton
                                {
                                    Icon = LucideIcons.ListFilter,
                                    Visible = Prop.Bind(OnFiles),
                                    Tooltip = L.T(s => s.FileBrowserShowHidden),
                                    Command = new Command(() =>
                                    {
                                        if (browsers.Active.Value is { } browser)
                                            browser.SetShowHidden(!browser.ShowHidden.Value);
                                    }),
                                },
                                new LocalChangesHeaderActionButton
                                {
                                    Icon = Prop.Bind<string?>(() =>
                                        OnFiles() ? LucideIcons.Branch : LucideIcons.Files),
                                    Tooltip = L.T(s =>
                                        OnFiles() ? s.BranchesShowBranches : s.BranchesShowFiles),
                                    Command = new Command(() => pane.Value =
                                        OnFiles() ? SidebarPane.Branches : SidebarPane.Files),
                                },
                            ],
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}

/// <summary>
/// Branch icon, "on"/"at" prefix, and branch name; hidden when there's no branch. While a checkout is
/// switching branches the icon turns into a spinner and the prefix reads "switching to", so the app's
/// most prominent branch claim never states a pending move as settled fact.
/// </summary>
internal sealed record BranchLabel : Widget
{
    public required IReadable<string?> BranchName { get; init; }
    public required IReadable<bool> IsDetached { get; init; }
    public required IReadable<bool> IsSwitching { get; init; }
    public required IReadable<float> SwitchRotation { get; init; }

    protected override IWidget Build(Context ctx) => new Padding
    {
        Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm },
        Visible = BranchName.Bind(n => !string.IsNullOrEmpty(n)),
        Children =
        [
            new Row
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new Text
                    {
                        Value = Prop.Bind(() => IsSwitching.Value ? LucideIcons.Loader : LucideIcons.Branch),
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = FontSize.Heading,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText),
                        Rotation = Prop.Bind(() => IsSwitching.Value ? SwitchRotation.Value : 0f),
                    },
                    new Text
                    {
                        Value = L.T(s => IsSwitching.Value ? s.BranchesHeaderSwitchingTo
                            : IsDetached.Value ? s.BranchesHeaderAt
                            : s.BranchesHeaderOn),
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.BranchesHeader.PrefixText),
                    },
                    new Text
                    {
                        Value = Prop.Bind(BranchName),
                        FontSize = FontSize.Heading,
                        Weight = FontWeight.Bold,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText),
                    },
                ],
            },
        ],
    };
}

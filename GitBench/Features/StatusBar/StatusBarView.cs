using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's
/// South region). Left side shows ambient repo context — active repo, current branch, and
/// ahead/behind counts; right side holds the theme toggle and the running build version.
/// </summary>
internal sealed record StatusBarView : Widget
{
    private const int BarHeight = Sizes.RowHeight;
    private const int HorizontalPadding = 8;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<StatusBarViewModel>();

        var left = new Row
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                Segment(LucideIcons.FolderGit2, Prop.Bind(vm.HasActiveRepo), Prop.Bind(vm.RepoName)),
                Segment(LucideIcons.Branch, Prop.Bind(vm.HasBranch), Prop.Bind(vm.Branch)),
                Segment(LucideIcons.ChevronUp, Prop.Bind(vm.ShowAhead), Prop.Bind<string?>(() => vm.AheadText.Value)),
                Segment(LucideIcons.ChevronDown, Prop.Bind(vm.ShowBehind), Prop.Bind<string?>(() => vm.BehindText.Value)),
                new IdentityChipButton
                {
                    Icon = vm.IdentityGlyph.Bind(string? (g) => g),
                    Label = vm.IdentityText.Bind(string? (t) => t),
                    Visible = Prop.Bind(vm.ShowIdentity),
                }.WithMenuController(rect =>
                {
                    var items = vm.BuildIdentityMenu();
                    if (items.Count == 0) return;
                    RepoBarContextMenu.Show(ctx, rect.TopLeft, items, MenuPlacement.Above);
                }),
            ],
        };

        // Fix the bar height on the outer box, not the inner one: the inner box also carries a 1px
        // top border, so giving it an explicit Height would make its measured size exceed its
        // laid-out size by the border and leave a 1px gap above the bar. Sizing the outer box and
        // letting the inner one fill the region keeps it flush against the content.
        return new Box
        {
            Height = BarHeight,
            Children =
            [
                new Box
                {
                    BorderSize = new BorderSizeStyle { Top = 1 },
                    Background = Theme.Color(s => s.StatusBar.Background),
                    BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.StatusBar.TopBorder }),
                    Children =
                    [
                        new Padding
                        {
                            Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
                            Children =
                            [
                                new Row
                                {
                                    Gap = Spacing.Md,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new StatusBarIconButton
                                        {
                                            Icon = Prop.Bind<string?>(() => vm.Theme.Value == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon),
                                            Command = vm.ToggleTheme,
                                        }.WithTooltip(L.T(s => s.StatusbarToggleThemeTooltip))
                                            .WithController<KbmController>(),
                                        new LanguageChipButton
                                        {
                                            Label = vm.ActiveLocale.Bind(string? (l) => StatusBarViewModel.Code(l)),
                                        }.WithMenuController(rect =>
                                            RepoBarContextMenu.Show(ctx, rect.TopLeft, vm.BuildLanguageMenu(), MenuPlacement.Above)),
                                        new Grow { Child = left },
                                        new Text
                                        {
                                            FontSize = FontSize.Caption,
                                            VAlign = TextAlignment.Center,
                                            HAlign = TextAlignment.End,
                                            Color = Theme.Color(s => s.StatusBar.Text),
                                            // Brief inline result of a manual check ("up to date" / "failed"); hidden when empty.
                                            Value = Prop.Bind<string?>(() => vm.UpdateCheckFeedback.Value ?? string.Empty),
                                            Visible = vm.UpdateCheckFeedback.Bind(m => !string.IsNullOrEmpty(m)),
                                        },
                                        new StatusBarIconButton
                                        {
                                            Icon = Prop.Bind<string?>(() => vm.IsCheckingUpdates.Value ? LucideIcons.Loader : LucideIcons.Fetch),
                                            Rotation = Prop.Bind(vm.UpdateIconRotation),
                                            Command = vm.CheckForUpdates,
                                        }.WithTooltip(L.T(s => s.StatusbarCheckUpdatesTooltip))
                                            .WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = $"v{AppVersion.Display}",
                                            FontSize = FontSize.Caption,
                                            VAlign = TextAlignment.Center,
                                            HAlign = TextAlignment.End,
                                            Color = Theme.Color(s => s.StatusBar.Text),
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }

    // An icon glyph + label pair, both in the muted status-bar color; the row hides as a unit when
    // its data is absent.
    private static IWidget Segment(string glyph, Prop<bool> visible, Prop<string?> label) => new Row
    {
        Gap = Spacing.Xs,
        CrossAxis = CrossAxisAlignment.Center,
        Visible = visible,
        Children =
        [
            new Text
            {
                Value = glyph,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Body,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.StatusBar.Text),
            },
            new Text
            {
                Value = label,
                FontSize = FontSize.Caption,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.StatusBar.Text),
            },
        ],
    };
}

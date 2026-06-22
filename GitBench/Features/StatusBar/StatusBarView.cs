using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
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
    private const int BarHeight = 22;
    private const int HorizontalPadding = 8;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<StatusBarViewModel>();

        var left = new Row
        {
            Gap = 10,
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
                    MenuProvider = vm.BuildIdentityMenu,
                    Visible = Prop.Bind(vm.ShowIdentity),
                },
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
                                    Gap = 8,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new StatusBarIconButton
                                        {
                                            Icon = Prop.Bind<string?>(() => vm.Theme.Value == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon),
                                            Command = vm.ToggleTheme,
                                        }.WithTooltip("Toggle theme")
                                            .WithController<KbmController>(),
                                        new Grow { Child = left },
                                        new Text
                                        {
                                            FontSize = 11f,
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
                                        }.WithTooltip("Check for updates")
                                            .WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = $"v{AppVersion.Display}",
                                            FontSize = 11f,
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
        Gap = 4,
        CrossAxis = CrossAxisAlignment.Center,
        Visible = visible,
        Children =
        [
            new Text
            {
                Value = glyph,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 12f,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.StatusBar.Text),
            },
            new Text
            {
                Value = label,
                FontSize = 11f,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.StatusBar.Text),
            },
        ],
    };
}

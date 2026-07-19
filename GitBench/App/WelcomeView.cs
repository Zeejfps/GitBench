using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Full-window first-run screen shown while no repositories are open: the app logo and name,
/// a short greeting, and the two ways in — open a local repository or clone one.
/// </summary>
internal sealed record WelcomeView : Widget
{
    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Center
            {
                Child = new Column
                {
                    Gap = Spacing.Xl,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new AppLogo { Size = 96 },
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    Value = "Pecia",
                                    FontSize = FontSize.Title,
                                    Weight = FontWeight.Bold,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextStrong),
                                },
                                new Text
                                {
                                    Value = L.T(s => s.WelcomeGreeting),
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                            ],
                        },
                        new Row
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new ActionDialogButton
                                {
                                    Label = L.T(s => s.ReposMenuOpenFromFolder),
                                    Icon = LucideIcons.FolderOpen,
                                    Command = new Command(() => AddRepoMenu.OpenFromFolder(ctx)),
                                    Height = DialogFrame.DefaultButtonHeight,
                                    MinWidth = 180,
                                }.WithController<KbmController>(),
                                new SecondaryDialogButton
                                {
                                    Label = L.T(s => s.ReposMenuCloneRepository),
                                    Icon = LucideIcons.FolderGit2,
                                    Command = new Command(() => AddRepoMenu.ShowCloneDialog(ctx)),
                                    Height = DialogFrame.DefaultButtonHeight,
                                    MinWidth = 180,
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            },
        ],
    };
}

using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// "About GitBench" modal: app icon, name, version, a link to the repo, and copyright.
/// Opened from the macOS app menu (and reusable from a Help menu on other platforms).
/// </summary>
internal sealed record AboutDialog : Widget<DialogState>
{
    private const string RepoUrl = "https://github.com/Zeejfps/GitBench";

    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    protected override IWidget Build(Context ctx, DialogState state)
    {
        return new Box
        {
            Width = 360,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(s => s.DialogFrame.Background),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.DialogFrame.Border)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Height = Sizes.ControlHeight,
                                    MainAxis = MainAxisAlignment.End,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children = [new DialogCloseButton { OnClose = OnClose }],
                                },
                                new Column
                                {
                                    Gap = Spacing.Lg,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new AppLogo(),
                                        new Text
                                        {
                                            Value = "GitBench",
                                            FontSize = FontSize.Title,
                                            HAlign = TextAlignment.Center,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.DialogFrame.TitleText),
                                        },
                                        new Text
                                        {
                                            Value = $"v{AppVersion.Display}",
                                            HAlign = TextAlignment.Center,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.Palette.TextSecondary),
                                        },
                                        new ActionDialogButton
                                        {
                                            Label = L.T(s => s.AboutViewOnGithub),
                                            Role = DialogButtonRole.Primary,
                                            Command = new Command(() => ctx.Get<IPlatformShell>()?.OpenUrl(RepoUrl)),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                        }.WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = L.T(s => s.AboutCopyright),
                                            HAlign = TextAlignment.Center,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.Palette.TextMuted),
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

}

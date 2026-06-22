using GitBench.Controls;
using GitBench.Controls.Dialogs;
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

    /// <summary>
    /// Image id of the app icon, set by startup once it's loaded into the canvas. Null when the
    /// icon couldn't be loaded — the dialog then falls back to a glyph rather than referencing an
    /// unknown id (which would throw when the ImageView measures).
    /// </summary>
    public static string? IconImageId { get; set; }

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
                            Gap = 12,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Height = 28,
                                    MainAxis = MainAxisAlignment.End,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children = [new DialogCloseButton { OnClose = OnClose }],
                                },
                                new Column
                                {
                                    Gap = 10,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        BuildLogo(),
                                        new Text
                                        {
                                            Value = "GitBench",
                                            FontSize = 22,
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
                                        new DialogButtonWidget
                                        {
                                            Label = "View on GitHub",
                                            Role = DialogButtonRole.Primary,
                                            Command = new Command(() => ctx.Get<IPlatformShell>()?.OpenUrl(RepoUrl)),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                        }.WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = "© 2026 Zee Vasilyev",
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

    private static IWidget BuildLogo()
    {
        if (IconImageId != null)
            return new Image { ImageId = IconImageId, Width = 84, Height = 84 };

        return new Box
        {
            Width = 84,
            Height = 84,
            Children =
            [
                new Text
                {
                    Value = LucideIcons.FolderGit2,
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 60,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.Accent),
                },
            ],
        };
    }
}

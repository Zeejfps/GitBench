using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Settings;

internal sealed record SettingsDialog : Widget<DialogState>
{
    public const string ThemePickerId = SettingsSections.ThemePickerId;
    public const string LanguagePickerId = SettingsSections.LanguagePickerId;
    public const string UiScalePickerId = SettingsSections.UiScalePickerId;
    public const string UntrackedCacheId = SettingsSections.UntrackedCacheId;
    public const string LanguageServersId = SettingsSections.LanguageServersId;
    public const string KeyboardShortcutsId = SettingsSections.KeyboardShortcutsId;

    private const float DialogHeight = 600f;

    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    protected override IWidget Build(Context ctx, DialogState state) => new Box
    {
        Width = DialogFrame.WidthStandard,
        Height = DialogHeight,
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
                                CrossAxis = CrossAxisAlignment.Center,
                                Children =
                                [
                                    new Grow
                                    {
                                        Child = new Text
                                        {
                                            Value = L.T(s => s.SettingsTitle),
                                            FontSize = FontSize.Title,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.DialogFrame.TitleText),
                                        },
                                    },
                                    new DialogCloseButton { OnClose = OnClose },
                                ],
                            },
                            new Grow
                            {
                                Child = new DialogScrollRegion
                                {
                                    FillParent = true,
                                    Content = new Padding
                                    {
                                        // Keeps the controls off the scrollbar when it appears.
                                        Amount = new PaddingStyle { Right = Spacing.Sm },
                                        Children = [new SettingsSections { OnClose = OnClose }],
                                    },
                                },
                            },
                        ],
                    },
                ],
            },
        ],
    };
}

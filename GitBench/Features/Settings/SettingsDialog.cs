using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.LanguageServers;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

internal sealed record SettingsDialog : Widget<DialogState>
{
    public const string ThemePickerId = "settings-theme";
    public const string LanguagePickerId = "settings-language";
    public const string UntrackedCacheId = "settings-untracked-cache";
    public const string LanguageServersId = "settings-language-servers";

    private const float ControlWidth = 160f;

    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    protected override IWidget Build(Context ctx, DialogState state)
    {
        var themeMode = ctx.Require<State<ThemeMode>>();
        var locale = ctx.Require<State<Locale>>();
        var untrackedCache = ctx.Require<State<bool>>();
        var loc = ctx.Localization();

        return new Box
        {
            Width = DialogFrame.WidthStandard,
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
                                SectionHeader(L.T(s => s.SettingsAppearance)),
                                SettingRow(
                                    L.T(s => s.SettingsTheme),
                                    L.T(s => s.SettingsThemeDesc),
                                    new DropdownWidget
                                    {
                                        Id = ThemePickerId,
                                        Width = ControlWidth,
                                        Height = Sizes.ControlHeight,
                                        Children =
                                        [
                                            new Grow
                                            {
                                                Child = new Text
                                                {
                                                    Value = Prop.Bind<string?>(
                                                        () => ThemeLabel(loc.Strings.Value, themeMode.Value)),
                                                    FontSize = FontSize.Caption,
                                                    VAlign = TextAlignment.Center,
                                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                                },
                                            },
                                        ],
                                    }.WithMenuController(rect => RepoBarContextMenu.Show(
                                        ctx, rect.BottomLeft, ThemeMenu(loc.Strings.Value, themeMode)))),
                                SettingRow(
                                    L.T(s => s.SettingsLanguage),
                                    L.T(s => s.SettingsLanguageDesc),
                                    new DropdownWidget
                                    {
                                        Id = LanguagePickerId,
                                        Width = ControlWidth,
                                        Height = Sizes.ControlHeight,
                                        Children =
                                        [
                                            new Grow
                                            {
                                                Child = new Text
                                                {
                                                    Value = Prop.Bind<string?>(
                                                        () => LocaleOptions.Endonym(locale.Value)),
                                                    FontSize = FontSize.Caption,
                                                    VAlign = TextAlignment.Center,
                                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                                },
                                            },
                                        ],
                                    }.WithMenuController(rect => RepoBarContextMenu.Show(
                                        ctx, rect.BottomLeft, LanguageMenu(locale)))),
                                SectionHeader(L.T(s => s.SettingsRepository)),
                                SettingRow(
                                    L.T(s => s.SettingsUntrackedCache),
                                    L.T(s => s.SettingsUntrackedCacheDesc),
                                    new CheckboxWidget
                                    {
                                        Id = UntrackedCacheId,
                                        Checked = untrackedCache,
                                        Height = Sizes.RowHeight,
                                    }.WithController<KbmController>()),
                                SectionHeader(L.T(s => s.SettingsCodeIntelligence)),
                                SettingRow(
                                    L.T(s => s.LanguageServersTitle),
                                    L.T(s => s.SettingsLanguageServersDesc),
                                    new SecondaryDialogButton
                                    {
                                        Id = LanguageServersId,
                                        Label = L.T(s => s.SettingsConfigure),
                                        Command = new Command(() => OpenLanguageServers(ctx)),
                                        Height = Sizes.ControlHeight,
                                    }.WithController<KbmController>()),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private void OpenLanguageServers(Context ctx)
    {
        var bus = ctx.Get<IMessageBus>();
        if (bus is null) return;
        OnClose();
        bus.Broadcast(new ShowDialogMessage(onClose =>
            new LanguageServersDialog { OnClose = onClose }));
    }

    private static IWidget SectionHeader(Prop<string?> value) => new Text
    {
        Value = value,
        FontSize = FontSize.Caption,
        Weight = FontWeight.Bold,
        Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
    };

    private static IWidget SettingRow(Prop<string?> label, Prop<string?> description, IWidget control) => new Row
    {
        Gap = Spacing.Lg,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Grow
            {
                Child = new Column
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Text
                        {
                            Value = label,
                            Color = Theme.Color(s => s.DialogBody.RowText),
                        },
                        new Text
                        {
                            Value = description,
                            Wrap = TextWrap.Wrap,
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        },
                    ],
                },
            },
            control,
        ],
    };

    private static string ThemeLabel(Strings strings, ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => strings.SettingsThemeDark,
        ThemeMode.Light => strings.SettingsThemeLight,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No label is defined for this theme."),
    };

    private static IReadOnlyList<RepoBarContextMenu.Item> ThemeMenu(Strings strings, State<ThemeMode> themeMode)
    {
        var active = themeMode.Value;
        return
        [
            new(strings.SettingsThemeDark, () => themeMode.Value = ThemeMode.Dark, Checked: active == ThemeMode.Dark),
            new(strings.SettingsThemeLight, () => themeMode.Value = ThemeMode.Light, Checked: active == ThemeMode.Light),
        ];
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> LanguageMenu(State<Locale> locale)
    {
        var active = locale.Value;
        var items = new List<RepoBarContextMenu.Item>(LocaleOptions.All.Count);
        foreach (var option in LocaleOptions.All)
        {
            var target = option.Locale;
            items.Add(new RepoBarContextMenu.Item(
                option.Endonym,
                () => locale.Value = target,
                Checked: active == target));
        }

        return items;
    }
}

using GitBench.App;
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

/// <summary>The general settings page: appearance, repository and editor preferences.</summary>
internal sealed record SettingsSections : Widget
{
    public const string ThemePickerId = "settings-theme";
    public const string LanguagePickerId = "settings-language";
    public const string UiScalePickerId = "settings-ui-scale";
    public const string UntrackedCacheId = "settings-untracked-cache";
    public const string LanguageServersId = "settings-language-servers";

    private const float ControlWidth = 160f;

    /// <summary>Closes the settings dialog before a row opens a dialog of its own.</summary>
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var themeMode = ctx.Require<State<ThemeMode>>();
        var locale = ctx.Require<State<Locale>>();
        var uiScale = ctx.Require<State<UiScale>>();
        var untrackedCache = ctx.Require<State<bool>>();
        var loc = ctx.Localization();

        return new Column
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new SettingsSectionHeader { Value = L.T(s => s.SettingsAppearance) },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsTheme),
                    Description = L.T(s => s.SettingsThemeDesc),
                    Control = new SettingsDropdown
                    {
                        Id = ThemePickerId,
                        Width = ControlWidth,
                        Height = Sizes.ControlHeight,
                        Selected = Prop.Bind<string?>(
                            () => ThemeLabel(loc.Strings.Value, themeMode.Value)),
                        Options = () => ThemeMenu(loc.Strings.Value, themeMode),
                    },
                },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsLanguage),
                    Description = L.T(s => s.SettingsLanguageDesc),
                    Control = new SettingsDropdown
                    {
                        Id = LanguagePickerId,
                        Width = ControlWidth,
                        Height = Sizes.ControlHeight,
                        Selected = Prop.Bind<string?>(
                            () => LocaleOptions.Endonym(locale.Value)),
                        Options = () => LanguageMenu(locale),
                    },
                },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsUiScale),
                    Description = L.T(s => s.SettingsUiScaleDesc),
                    Control = new SettingsDropdown
                    {
                        Id = UiScalePickerId,
                        Width = ControlWidth,
                        Height = Sizes.ControlHeight,
                        Selected = Prop.Bind<string?>(() => uiScale.Value.Label),
                        Options = () => UiScaleMenu(uiScale),
                    },
                },
                new SettingsSectionHeader { Value = L.T(s => s.SettingsRepository) },
                new SettingsRow
                {
                    Label = L.T(s => s.SettingsUntrackedCache),
                    Description = L.T(s => s.SettingsUntrackedCacheDesc),
                    Control = new CheckboxWidget
                    {
                        Id = UntrackedCacheId,
                        Checked = untrackedCache,
                        Height = Sizes.RowHeight,
                    }.WithController<KbmController>(),
                },
                new SettingsSectionHeader { Value = L.T(s => s.SettingsCodeIntelligence) },
                new SettingsRow
                {
                    Label = L.T(s => s.LanguageServersTitle),
                    Description = L.T(s => s.SettingsLanguageServersDesc),
                    Control = new SecondaryDialogButton
                    {
                        Id = LanguageServersId,
                        Label = L.T(s => s.SettingsConfigure),
                        Command = new Command(() => OpenLanguageServers(ctx)),
                        Height = Sizes.ControlHeight,
                    }.WithController<KbmController>(),
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

    private static IReadOnlyList<RepoBarContextMenu.Item> UiScaleMenu(State<UiScale> uiScale)
    {
        var active = uiScale.Value;
        var items = new List<RepoBarContextMenu.Item>(UiScale.All.Count);
        foreach (var option in UiScale.All)
        {
            var target = option;
            items.Add(new RepoBarContextMenu.Item(
                target.Label,
                () => uiScale.Value = target,
                Checked: active == target));
        }

        return items;
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

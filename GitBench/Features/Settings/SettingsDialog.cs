using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Assistant;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Settings;

internal sealed record SettingsDialog : Widget<SettingsDialogState>
{
    public const string ThemePickerId = SettingsSections.ThemePickerId;
    public const string LanguagePickerId = SettingsSections.LanguagePickerId;
    public const string UiScalePickerId = SettingsSections.UiScalePickerId;
    public const string UntrackedCacheId = SettingsSections.UntrackedCacheId;
    public const string LanguageServersId = SettingsSections.LanguageServersId;
    public const string GeneralTabId = "settings-tab-general";
    public const string KeyboardTabId = "settings-tab-keyboard";
    public const string AgentTabId = "settings-tab-agent";
    public const string ConnectionsTabId = "settings-tab-connections";

    internal const float DialogHeight = 600f;

    public required Action OnClose { get; init; }
    public bool HostedInWindow { get; init; }

    protected override SettingsDialogState CreateState(Context ctx) => new(
        OnClose, ctx.Require<IAssistantSessionStore>(), ctx.Localization(), ctx.Require<IMessageBus>(),
        ctx.Require<InputSystem>());

    protected override IWidget Build(Context ctx, SettingsDialogState state) => new Box
    {
        Width = HostedInWindow ? default(Prop<float>) : DialogFrame.WidthWide,
        Height = HostedInWindow ? default(Prop<float>) : DialogHeight,
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
                                            Id = "settings-window-title",
                                            Value = L.T(s => s.SettingsTitle),
                                            FontSize = FontSize.Title,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.DialogFrame.TitleText),
                                        },
                                    },
                                    new DialogCloseButton { OnClose = OnClose },
                                ],
                            },
                            new Box
                            {
                                Height = 36,
                                BorderSize = new BorderSizeStyle { Bottom = 1 },
                                BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
                                Children =
                                [
                                    new HorizontalScrollArea
                                    {
                                        VerticalWheelPans = true,
                                        Child = new Row
                                        {
                                            Gap = Spacing.Sm,
                                            CrossAxis = CrossAxisAlignment.Stretch,
                                            Children =
                                            [
                                                Tab(GeneralTabId, L.T(s => s.SettingsGeneral), SettingsPage.General, state),
                                                Tab(KeyboardTabId, L.T(s => s.SettingsKeyboard), SettingsPage.Keyboard, state),
                                                Tab(AgentTabId, L.T(s => s.SettingsAgent), SettingsPage.Agent, state),
                                                Tab(ConnectionsTabId, L.T(s => s.SettingsAgentConnections), SettingsPage.Connections, state),
                                            ],
                                        },
                                    },
                                ],
                            },
                            new Grow
                            {
                                Child = new Switch<SettingsPage>
                                {
                                    Value = state.Page,
                                    KeepAlive = true,
                                    // The shortcut editor owns its scroll region so search and reset
                                    // stay visible while the list scrolls.
                                    Case = page => page == SettingsPage.Keyboard
                                        ? new KeyboardShortcutsEditor()
                                        : new ScrollRegion
                                        {
                                            FillParent = true,
                                            Content = new Padding
                                            {
                                                Amount = new PaddingStyle { Right = Spacing.Sm },
                                                Children = [PageContent(page, state)],
                                            },
                                        },
                                },
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static IWidget Tab(string id, Prop<string?> label, SettingsPage page, SettingsDialogState state) =>
        new Box
        {
            Id = id,
            BorderSize = new BorderSizeStyle { Bottom = 2 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                Bottom = state.Page.Value == page ? s.Palette.Accent : s.DialogFrame.Background,
            }),
            Children =
            [
                new ButtonWidget
                {
                    Command = new Command(() => state.SelectPage(page)),
                    ContentInset = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                    Children =
                    [
                        // Category names stay at their natural width. File-tab ellipsis would
                        // truncate a fitting name when scaled layout loses a fraction of a pixel.
                        new Text
                        {
                            Value = label,
                            FontSize = FontSize.Body,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(s => state.Page.Value == page
                                ? s.Palette.TextPrimary : s.Palette.TextSecondary),
                        },
                    ],
                }.WithController<KbmController>(),
            ],
        };

    private IWidget PageContent(SettingsPage page, SettingsDialogState state) => page switch
    {
        SettingsPage.General => Page(L.T(s => s.SettingsGeneralDesc), new SettingsSections { OnClose = OnClose }),
        SettingsPage.Agent => Page(L.T(s => s.SettingsAgentDesc),
            new Provide<AssistantViewModel>
            {
                Value = state.Agent,
                Child = new Column
                {
                    Gap = Spacing.Lg,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new SettingsSectionHeader { Value = L.T(s => s.AssistantSettingsTitle) },
                        new Text
                        {
                            Value = Prop.Bind<string?>(() => state.ActiveConnection.Value),
                            Wrap = TextWrap.Wrap,
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        },
                        new AssistantSettingsCard { Embedded = true },
                    ],
                },
            }),
        SettingsPage.Connections => Page(L.T(s => s.SettingsConnectionsDesc), new AgentConnectionsSettingsSection()),
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };

    private static IWidget Page(Prop<string?> description, IWidget content) => new Column
    {
        Gap = Spacing.Lg,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new Text
            {
                Value = description,
                Wrap = TextWrap.Wrap,
                FontSize = FontSize.Caption,
                Color = Theme.Color(s => s.Palette.TextMuted),
            },
            content,
        ],
    };
}

internal enum SettingsPage { General, Keyboard, Agent, Connections }

/// <summary>Owns the dialog's edit session; chat retains its own drafts and visibility.</summary>
internal sealed class SettingsDialogState : IDialog, IDisposable
{
    private readonly Action _close;
    private readonly InputSystem _input;
    public State<SettingsPage> Page { get; } = new(SettingsPage.General);
    public AssistantViewModel Agent { get; }
    public Derived<string> ActiveConnection { get; }

    public SettingsDialogState(Action close, IAssistantSessionStore store, ILocalizationService loc, IMessageBus bus,
        InputSystem input)
    {
        _close = close;
        _input = input;
        Agent = new AssistantViewModel(store, loc, bus);
        Agent.ResetSettings.Execute();
        ActiveConnection = new Derived<string>(() => loc.Strings.Value.SettingsAgentActive(
            store.Settings.Value.Provider.DisplayName,
            store.Settings.Value.Model ?? store.Settings.Value.Provider.ChatModel));
    }

    public void SelectPage(SettingsPage page)
    {
        if (Page.Value == page) return;
        // Cached pages stay mounted. Release their input before hiding them so a search field
        // or shortcut recorder cannot consume keys intended for the new page.
        if (_input.FocusedComponent is { } focused) _input.Blur(focused);
        Page.Value = page;
    }

    // Enter in an ordinary settings field must not dismiss the whole dialog or save credentials.
    public void Confirm() { }
    public void Cancel() => _close();

    public void Dispose()
    {
        ActiveConnection.Dispose();
        Agent.Dispose();
        Page.Dispose();
    }
}

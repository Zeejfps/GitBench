using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// The assistant as a self-contained surface: who you are talking to, the conversation, and the way
/// to add to it. Owns no placement of its own, so the overlay — or a future dock or window — can
/// put the same panel wherever it belongs.
/// </summary>
internal sealed record AssistantPanel : Widget
{
    public const string CloseId = "assistant-close";
    public const string SettingsId = "assistant-settings";
    public const string ClearId = "assistant-clear";

    /// <summary>The strip the panel is dragged by. The body below it scrolls and selects text, so the
    /// grab area is explicit rather than "anywhere that isn't a control".</summary>
    public const string HeaderId = "assistant-header";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();
        var placement = ctx.Require<AssistantPanelPlacement>();
        var input = ctx.Require<InputSystem>();

        var header = new Box
        {
            Id = HeaderId,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Md, Top = Spacing.Md, Bottom = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new AssistantMark { Size = 16 },
                                new Text
                                {
                                    Value = L.T(s => s.AssistantTitle),
                                    Weight = FontWeight.Bold,
                                    FontSize = FontSize.Body,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                                new Grow { Child = Empty.Widget },
                                new AssistantProviderSwitcher(),
                                new ButtonWidget
                                {
                                    Id = ClearId,
                                    Style = ButtonStyle.BareMuted,
                                    Command = vm.ClearConversation,
                                    Children = [new ButtonIcon { Value = LucideIcons.Trash, FontSize = FontSize.Body }],
                                }
                                .WithTooltip(L.T(s => s.AssistantClear))
                                .WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Id = SettingsId,
                                    Style = ButtonStyle.BareMuted,
                                    Command = vm.OpenSettings,
                                    Children = [new ButtonIcon { Value = LucideIcons.Settings, FontSize = FontSize.Body }],
                                }
                                .WithTooltip(L.T(s => s.AssistantSettingsOpen))
                                .WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Id = CloseId,
                                    Style = ButtonStyle.BareMuted,
                                    Command = vm.Close,
                                    Children = [new ButtonIcon { Value = LucideIcons.X, FontSize = FontSize.Body }],
                                }
                                .WithTooltip(L.T(s => s.AssistantClose))
                                .WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        }
        .WithController(input, view => new AssistantPanelMoveController(placement, input, view));

        return new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                header,
                new Grow { Child = new AssistantTranscript() },
                // The connection card takes the composer's place until one resolves, so the panel
                // never offers an input that cannot send.
                new Show
                {
                    When = vm.ShowSettings,
                    Then = static () => new AssistantSettingsCard(),
                    Else = static () => new AssistantComposer(),
                },
            ],
        };
    }
}

/// <summary>
/// Who the panel is talking to, and the way to talk to someone else: the active provider's name,
/// opening the list of the ones already set up. Swapping between two configured providers is a
/// header click rather than a trip through the settings card.
/// </summary>
internal sealed record AssistantProviderSwitcher : Widget
{
    public const string ButtonId = "assistant-provider-switch";

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<AssistantViewModel>();

        var button = new ButtonWidget
        {
            Id = ButtonId,
            // The press belongs to the menu controller below; this satisfies the button's command.
            Command = new Command(static () => { }),
            Style = ButtonStyle.BareMuted,
            Children =
            [
                new ButtonLabel { Value = Prop.Bind<string?>(() => vm.ActiveProviderName.Value) },
                new ButtonIcon { Value = LucideIcons.ChevronDown, FontSize = FontSize.Caption },
            ],
        };

        return button
            .WithTooltip(L.T(s => s.AssistantProviderSwitch))
            .WithMenuController(rect =>
                RepoBarContextMenu.Show(ctx, rect.BottomLeft, vm.BuildProviderSwitcher()));
    }
}

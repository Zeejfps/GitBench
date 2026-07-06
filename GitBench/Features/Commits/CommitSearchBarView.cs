using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Slim filter bar shown above the commit list: a rounded search box (leading search icon, text
/// input, trailing clear (✕) button once there's text) and a remote filter toggle beside it that
/// hides branches existing only on the remote. Interactions are surfaced through the init
/// callbacks; the panel forwards them to <see cref="CommitsView.Core"/>. It deliberately holds no
/// view model — the <see cref="CommitsViewModel"/> is owned by <see cref="CommitsView.Core"/>.
/// </summary>
internal sealed record CommitSearchBarView : Widget
{
    private const float BarHeight = 36f;

    /// <summary>Fires on every edit (including a programmatic clear) with the current text.</summary>
    public required Action<string> OnQueryChanged { get; init; }

    /// <summary>True while the graph is hiding remote-only branches; tints the filter toggle.</summary>
    public required IReadable<bool> RemoteFilterActive { get; init; }

    /// <summary>Flips the remote filter on/off.</summary>
    public required Action OnToggleRemoteFilter { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var inputSystem = ctx.Require<InputSystem>();

        var textInput = DialogFrame.TextInput(ctx);
        textInput.Bind(ctx.Localization().Strings, s => textInput.PlaceholderText = s.CommitsSearchPlaceholder);

        void Clear()
        {
            if (textInput.Text.Length == 0) return;
            textInput.Clear(); // fires TextValue → OnQueryChanged("")
        }

        var controller = new SearchInputKbmController(textInput, inputSystem, ctx.Get<IClipboard>()) { OnEscape = Clear };
        textInput.UseController(inputSystem, controller);
        textInput.Bind(textInput.TextValue, OnQueryChanged);

        // Icon-only clear button: same chrome as the status-bar icon buttons, sized down for the bar.
        var clear = new StatusBarIconButton
        {
            Icon = LucideIcons.X,
            Command = new Command(Clear),
            BoxWidth = 18,
            BoxHeight = 18,
            IconSize = 12,
            Visible = textInput.TextValue.Bind(t => t.Length > 0),
        }
            .WithTooltip(L.T(s => s.CommitsSearchClearTooltip))
            .WithController<KbmController>();

        // Engaged-toggle styling (accent tint while active) so it reads as a filter that stays on,
        // not a one-shot action.
        var strings = ctx.Localization().Strings;
        var remoteFilter = new ButtonWidget
        {
            Style = ButtonStyle.Bare(s => Theme.Color(t => RemoteFilterActive.Value
                ? t.CommitsView.FilterToggleActive
                : s.Enabled.Value && s.Hovered.Value ? t.CommitsView.RowText : t.CommitsView.RowTextDim)),
            Command = new Command(OnToggleRemoteFilter),
            Children = [new ButtonIcon { Value = LucideIcons.ListFilter, FontSize = FontSize.Body }],
        }
            .WithTooltip(Prop.Bind(() => (string?)(RemoteFilterActive.Value
                ? strings.Value.CommitsFilterRemoteShowTooltip
                : strings.Value.CommitsFilterRemoteHideTooltip)))
            .WithController<KbmController>();

        return new Box
        {
            Height = BarHeight,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Background = Theme.Color(s => s.CommitsView.HeaderBackground),
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.CommitsView.HeaderBorderBottom }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = Spacing.Sm, Bottom = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = Spacing.Sm,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Box
                                    {
                                        BorderSize = BorderSizeStyle.All(1),
                                        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                                        Background = Theme.Color(s => s.TextInput.Background),
                                        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
                                        Children =
                                        [
                                            new Padding
                                            {
                                                Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm, Top = Spacing.Hair, Bottom = Spacing.Hair },
                                                Children =
                                                [
                                                    new Row
                                                    {
                                                        CrossAxis = CrossAxisAlignment.Center,
                                                        Gap = Spacing.Md,
                                                        Children =
                                                        [
                                                            new Text
                                                            {
                                                                FontFamily = LucideIcons.FontFamily,
                                                                FontSize = FontSize.Default,
                                                                Value = LucideIcons.Search,
                                                                HAlign = TextAlignment.Center,
                                                                VAlign = TextAlignment.Center,
                                                                Color = Theme.Color(s => s.CommitsView.RowTextDim),
                                                            },
                                                            new Grow { Child = new Raw { View = textInput } },
                                                            clear,
                                                        ],
                                                    },
                                                ],
                                            },
                                        ],
                                    },
                                },
                                remoteFilter,
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

// Text-input controller for the filter box. Escape clears the query; OnFocusLost ends the edit
// session so the field stops intercepting keys once focus moves elsewhere (e.g. clicking a commit
// row steals focus via StealFocus, which would otherwise leave the input still "editing").
internal sealed class SearchInputKbmController : BaseTextInputKbmController
{
    private readonly TextInputView _input;

    public Action? OnEscape { get; set; }

    public SearchInputKbmController(TextInputView input, InputSystem inputSystem, IClipboard? clipboard)
        : base(input, inputSystem, clipboard)
    {
        _input = input;
    }

    protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
    {
        if (e.Key == KeyboardKey.Escape)
        {
            e.Consume();
            OnEscape?.Invoke();
            return;
        }
        base.OnKeyboardKeyPressed(ref e);
    }

    public override void OnFocusLost()
    {
        _input.StopEditing();
    }
}

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
/// Slim filter bar shown above the commit list: a single rounded search box containing a leading
/// search icon, the text input, and a trailing clear (✕) button that appears once there's text.
/// Edits are surfaced through <see cref="OnQueryChanged"/>; the panel forwards those to
/// <see cref="CommitsView.Core.SetSearchQuery"/>. It deliberately holds no view model — the
/// <see cref="CommitsViewModel"/> is owned by <see cref="CommitsView.Core"/>.
/// </summary>
internal sealed record CommitSearchBarView : Widget
{
    private const float BarHeight = 36f;

    /// <summary>Fires on every edit (including a programmatic clear) with the current text.</summary>
    public required Action<string> OnQueryChanged { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var inputSystem = ctx.Require<InputSystem>();

        var textInput = DialogFrame.TextInput(ctx);
        textInput.PlaceholderText = ctx.Localization().Strings.Value.CommitsSearchPlaceholder;

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
                    Amount = new PaddingStyle { Left = 8, Right = 8, Top = 5, Bottom = 5 },
                    Children =
                    [
                        new Box
                        {
                            BorderSize = BorderSizeStyle.All(1),
                            BorderRadius = BorderRadiusStyle.All(4),
                            Background = Theme.Color(s => s.TextInput.Background),
                            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
                            Children =
                            [
                                new Padding
                                {
                                    Amount = new PaddingStyle { Left = 8, Right = 6, Top = 2, Bottom = 2 },
                                    Children =
                                    [
                                        new Row
                                        {
                                            CrossAxis = CrossAxisAlignment.Center,
                                            Gap = 8,
                                            Children =
                                            [
                                                new Text
                                                {
                                                    FontFamily = LucideIcons.FontFamily,
                                                    FontSize = 14,
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

using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Slim filter bar shown above the commit list: a single rounded search box containing a leading
/// search icon, the text input, and a trailing clear (✕) button that appears once there's text.
/// Changes are surfaced via <see cref="QueryChanged"/>; the panel forwards those to
/// <see cref="CommitsView.Core.SetSearchQuery"/>. It deliberately holds no view model — the
/// <see cref="CommitsViewModel"/> is owned by <see cref="CommitsView.Core"/>.
/// </summary>
internal sealed class CommitSearchBarView : ContainerView
{
    private const float BarHeight = 36f;

    private readonly TextInputView _input;
    private readonly SearchInputKbmController _controller;
    private readonly View _clear;

    // Fires on every edit (including programmatic clear) with the current text.
    public event Action<string>? QueryChanged;

    public CommitSearchBarView(Context ctx)
    {
        Height = BarHeight;

        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();

        _input = DialogFrame.TextInput();
        _input.PlaceholderText = "Filter commits…";
        _controller = new SearchInputKbmController(_input, input, ctx.Get<IClipboard>()) { OnEscape = Clear };
        _input.UseController(input, _controller);

        var icon = new TextView(ctx.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            Text = LucideIcons.Search,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(theme, s => s.CommitsView.RowTextDim);

        _clear = BuildClearButton(ctx, input, theme, Clear);
        _clear.IsVisible = false;

        // Subscribe only after _clear exists — TextValue fires immediately with the current value.
        this.Bind(_input.TextValue, OnTextChanged);

        // Icon (left) · input (grows) · clear (right), all inside one bordered, rounded box.
        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = new PaddingStyle { Left = 8, Right = 6, Top = 2, Bottom = 2 },
            Children =
            {
                new FlexRowView
                {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Gap = 8,
                    Children =
                    {
                        icon,
                        new FlexItem { Grow = 1, Child = _input },
                        _clear,
                    },
                },
            },
        };
        box.BindThemedBackgroundColor(theme, s => s.TextInput.Background);
        box.BindThemedBorderColor(theme, s => BorderColorStyle.All(s.TextInput.Border));

        var root = new RectView
        {
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 5, Bottom = 5 },
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Children = { box },
        };
        root.BindThemedBackgroundColor(theme, s => s.CommitsView.HeaderBackground);
        root.BindThemedBorderColor(theme, s => new BorderColorStyle { Bottom = s.CommitsView.HeaderBorderBottom });

        AddChildToSelf(root);
    }

    // Small icon-only "clear filter" button. Chrome mirrors StatusBarIconButton (status-bar
    // palette, rounded hover fill) but carries a fixed X glyph and a click action.
    private static View BuildClearButton(Context ctx, InputSystem input, IThemeService<ThemeStyles> theme, Action onClick)
    {
        var hovered = new State<bool>(false);

        var label = new TextView(ctx.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            Text = LucideIcons.X,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindTextColor(() => hovered.Value
            ? theme.Styles.Value.StatusBar.IconHover
            : theme.Styles.Value.StatusBar.Icon);

        var button = new RectView
        {
            Width = 18,
            Height = 18,
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label },
        };
        button.BindBackgroundColor(() => hovered.Value
            ? theme.Styles.Value.StatusBar.IconHoverBackground
            : 0u);
        button.UseController(input, new KbmHandlers
        {
            OnClick = onClick,
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
        });
        button.Use(() => new Tooltip(button, ctx, "Clear filter", hovered, AlwaysEnabled));
        return button;
    }

    // The clear button is always actionable, so its tooltip never needs to gate on an enabled
    // state — Tooltip still requires an IReadable<bool>, so hand it a constant.
    private static readonly IReadable<bool> AlwaysEnabled = new State<bool>(true);

    private void OnTextChanged(string text)
    {
        _clear.IsVisible = text.Length > 0;
        QueryChanged?.Invoke(text);
    }

    private void Clear()
    {
        if (_input.Text.Length == 0) return;
        _input.Clear(); // fires TextValue → OnTextChanged("") → QueryChanged("")
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

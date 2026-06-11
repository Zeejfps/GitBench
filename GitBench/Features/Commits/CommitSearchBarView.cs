using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;

namespace GitBench.Features.Commits;

/// <summary>
/// Slim filter bar shown above the commit list: a single rounded search box containing a leading
/// search icon, the text input, and a trailing clear (✕) button that appears once there's text.
/// Changes are surfaced via <see cref="QueryChanged"/>; the panel forwards those to
/// <see cref="CommitsView.SetSearchQuery"/>. It deliberately holds no view model — the
/// <see cref="CommitsViewModel"/> is owned by <see cref="CommitsView"/>.
/// </summary>
internal sealed class CommitSearchBarView : ContainerView
{
    private const float BarHeight = 36f;

    private readonly TextInputView _input;
    private readonly SearchInputKbmController _controller;
    private readonly CommitSearchClearButton _clear;

    // Fires on every edit (including programmatic clear) with the current text.
    public event Action<string>? QueryChanged;

    public CommitSearchBarView()
    {
        Height = BarHeight;

        _input = DialogFrame.TextInput();
        _input.PlaceholderText = "Filter commits…";
        _controller = new SearchInputKbmController(_input) { OnEscape = Clear };
        _input.UseController(_ => _controller);

        var icon = new TextView(CompatUi.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            Text = LucideIcons.Search,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(s => s.CommitsView.RowTextDim);

        _clear = new CommitSearchClearButton(Clear) { IsVisible = false };

        // Subscribe only after _clear exists — TextValue fires immediately with the current value.
        _input.TextValue.Subscribe(OnTextChanged);

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
        box.BindThemedBackgroundColor(s => s.TextInput.Background);
        box.BindThemedBorderColor(s => BorderColorStyle.All(s.TextInput.Border));

        var root = new RectView
        {
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 5, Bottom = 5 },
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Children = { box },
        };
        root.BindThemedBackgroundColor(s => s.CommitsView.HeaderBackground);
        root.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.CommitsView.HeaderBorderBottom });

        AddChildToSelf(root);
    }

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

    public SearchInputKbmController(TextInputView input) : base(input, CompatUi.Input, CompatUi.Current.Get<ZGF.Gui.IClipboard>())
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

// Small icon-only "clear filter" button. Chrome mirrors StatusBarIconButton (status-bar palette,
// rounded hover fill) but carries a fixed X glyph and a click action.
internal sealed class CommitSearchClearButton : HoverableButton
{
    public CommitSearchClearButton(Action onClick) : base(onClick, tooltip: "Clear filter")
    {
        Width = 18;
        Height = 18;

        var label = new TextView(CompatUi.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            Text = LucideIcons.X,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => IsHovered.Value ? s.StatusBar.IconHover : s.StatusBar.Icon);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children = { label },
        };
        background.BindThemedBackgroundColor(s => IsHovered.Value ? s.StatusBar.IconHoverBackground : 0u);

        SetBackground(background);
    }
}

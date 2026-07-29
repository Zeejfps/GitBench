using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A multi-line text input that auto-grows with its content between <c>min</c> and <c>max</c>.
/// Once content exceeds <c>max</c>, the field caps at that height and a vertical scroll bar
/// is shown so the rest is reachable by scrolling.
///
/// The desired height is recomputed in <see cref="OnLayoutChildren"/> (passing the input's
/// laid-out width to <c>MeasureHeight</c>) and stored as <c>PreferredHeight</c>; the next
/// layout pass picks it up.
/// </summary>
internal sealed class GrowingDescriptionField : ContainerView
{
    private const float BoxBorderThickness = 1f;
    private const float BoxPaddingHorizontal = 6f;
    private const float BoxPaddingVertical = 4f;

    private readonly float _minHeight;
    private readonly float _maxHeight;

    private readonly TextInputView _input;
    private readonly FieldController _inputController;
    private readonly ScrollPane _scrollPane;
    private readonly VerticalScrollBarView _scrollBar;

    /// <summary>
    /// Claims plain Enter for the owner (send, commit) instead of breaking the line; Shift+Enter
    /// still inserts a newline, as does Enter when this is unset — which is how a field with no
    /// submit action of its own behaves.
    /// </summary>
    public Action? OnSubmit
    {
        get => _inputController.OnSubmit;
        set => _inputController.OnSubmit = value;
    }

    public string? PlaceholderText
    {
        get => _input.PlaceholderText;
        set => _input.PlaceholderText = value;
    }

    public ReadOnlySpan<char> Text => _input.Text;

    /// <summary>The multi-line text as an observable. See <see cref="TextInputView.TextValue"/>.</summary>
    public IReadable<string> TextValue => _input.TextValue;

    /// <summary>Two-way binds the field to a view model's read-only text + setter. Delegates
    /// to the inner input's <c>BindTwoWay</c>.</summary>
    public void BindTwoWay(IReadable<string> source, Action<string> sink)
        => _input.BindTwoWay(source, sink);

    public void BeginEditing() => _inputController.BeginEditing();
    public void EndEditing() => _inputController.EndEditing();

    public Action? OnTab
    {
        get => _inputController.OnTab;
        set => _inputController.OnTab = value;
    }

    public Action? OnShiftTab
    {
        get => _inputController.OnShiftTab;
        set => _inputController.OnShiftTab = value;
    }

    public void Clear() => _input.Clear();

    public void SetText(ReadOnlySpan<char> text) => _input.SetText(text);

    public GrowingDescriptionField(Context ctx, float minHeight, float maxHeight)
    {
        _minHeight = minHeight;
        _maxHeight = maxHeight;

        var theme = ctx.Theme();
        var inputSystem = ctx.Require<InputSystem>();

        _input = new TextInputView(ctx.Canvas)
        {
            TextVerticalAlignment = TextAlignment.Start,
            TextWrap = TextWrap.Wrap,
        };
        _input.BindThemed(theme, s =>
        {
            _input.BackgroundColor = s.TextInput.Background;
            _input.TextColor = s.TextInput.Text;
            _input.CaretColor = s.TextInput.Caret;
            _input.SelectionRectColor = s.TextInput.Selection;
            _input.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        _inputController = new FieldController(_input, inputSystem, ctx.Get<ZGF.Gui.IClipboard>()) { IsMultiLine = true };
        _input.UseController(inputSystem, _inputController);

        _scrollPane = new ScrollPane();
        _scrollPane.Children.Add(_input);
        _scrollPane.UseController(inputSystem, () => new ScrollPaneWheelController(_scrollPane));

        _scrollBar = ScrollBars.CreateVertical(ctx);

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All((int)BoxBorderThickness),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.ControlBorderRadius),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle
                    {
                        Left = (int)BoxPaddingHorizontal,
                        Right = (int)BoxPaddingHorizontal,
                        Top = (int)BoxPaddingVertical,
                        Bottom = (int)BoxPaddingVertical,
                    },
                    Children =
                    {
                        new BorderLayoutView
                        {
                            Center = _scrollPane,
                            East = _scrollBar,
                        },
                    },
                },
            },
        };
        box.BindThemedBackgroundColor(theme, s => s.TextInput.Background);
        box.BindThemedBorderColor(theme, s => BorderColorStyle.All(s.TextInput.Border));
        AddChildToSelf(box);

        this.Use(() => new ScrollSyncController(_scrollPane, _scrollBar));

        // Start at the min size; the first OnLayoutChildren pass will refine this.
        Height = _minHeight;
    }

    // Intercepts the newline the multi-line editor would insert, rather than the key event that
    // produced it: by the time the base calls this, an IME composition has already returned earlier
    // (its Enter picks a candidate and must never submit), and a pasted or typed newline arrives on
    // the span overload instead. Modifiers are recorded on the way past because Enter(char) doesn't
    // carry them.
    private sealed class FieldController : BaseTextInputKbmController
    {
        private const InputModifiers RelevantMask =
            InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

        private InputModifiers _modifiers;

        public FieldController(TextInputView textInput, InputSystem inputSystem, ZGF.Gui.IClipboard? clipboard)
            : base(textInput, inputSystem, clipboard)
        {
        }

        public Action? OnSubmit { get; set; }

        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            _modifiers = e.Modifiers;
            base.OnKeyboardKeyStateChanged(ref e);
        }

        protected override void Enter(char c)
        {
            if (c == '\n' && OnSubmit != null && (_modifiers & RelevantMask) == InputModifiers.None)
            {
                OnSubmit();
                return;
            }

            base.Enter(c);
        }
    }

    protected override void OnLayoutChildren()
    {
        base.OnLayoutChildren();

        // MeasureHeight(width) handles the height-for-width case directly now; pass the
        // input's laid-out width and cache the clamped desired height as PreferredHeight.
        var chrome = 2f * (BoxBorderThickness + BoxPaddingVertical);
        var contentHeight = _input.MeasureHeight(_input.Position.Width);
        var desired = Math.Clamp(contentHeight + chrome, _minHeight, _maxHeight);
        if (Math.Abs(desired - Height) > 0.5f)
        {
            // Setting PreferredHeight via SetField marks us IsSelfDirty, so the next frame's
            // layout re-runs OnLayoutSelf with the new value.
            Height = desired;
        }
    }
}

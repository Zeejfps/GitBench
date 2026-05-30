using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;

namespace GitGui;

/// <summary>
/// A multi-line text input that auto-grows with its content between <c>min</c> and <c>max</c>.
/// Once content exceeds <c>max</c>, the field caps at that height and a vertical scroll bar
/// is shown so the rest is reachable by scrolling.
///
/// The desired height is recomputed in <see cref="OnLayoutChildren"/> (passing the input's
/// laid-out width to <c>MeasureHeight</c>) and stored as <c>PreferredHeight</c>; the next
/// layout pass picks it up.
/// </summary>
internal sealed class GrowingDescriptionField : MultiChildView
{
    private const float BoxBorderThickness = 1f;
    private const float BoxPaddingHorizontal = 6f;
    private const float BoxPaddingVertical = 4f;

    private readonly float _minHeight;
    private readonly float _maxHeight;

    private readonly TextInputView _input;
    private readonly TextInputViewKbmController _inputController;
    private readonly ScrollPane _scrollPane;
    private readonly VerticalScrollBarView _scrollBar;

    public string? PlaceholderText
    {
        get => _input.PlaceholderText;
        set => _input.PlaceholderText = value;
    }

    public ReadOnlySpan<char> Text => _input.Text;

    public event Action? TextChanged
    {
        add => _input.TextChanged += value;
        remove => _input.TextChanged -= value;
    }

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

    public void SetText(ReadOnlySpan<char> text)
    {
        _input.Clear();
        if (text.Length > 0) _input.Enter(text);
    }

    public GrowingDescriptionField(float minHeight, float maxHeight)
    {
        _minHeight = minHeight;
        _maxHeight = maxHeight;

        _input = new TextInputView
        {
            TextVerticalAlignment = TextAlignment.Start,
            TextWrap = TextWrap.Wrap,
        };
        _input.BindThemed(s =>
        {
            _input.BackgroundColor = s.TextInput.Background;
            _input.TextColor = s.TextInput.Text;
            _input.CaretColor = s.TextInput.Caret;
            _input.SelectionRectColor = s.TextInput.Selection;
            _input.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        _inputController = new TextInputViewKbmController(_input) { IsMultiLine = true };
        _input.UseController(_ => _inputController);

        _scrollPane = new ScrollPane();
        _scrollPane.Children.Add(_input);
        _scrollPane.UseController(_ => new ScrollPaneWheelController(_scrollPane));

        _scrollBar = ScrollBars.CreateVertical();

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All((int)BoxBorderThickness),
            BorderRadius = BorderRadiusStyle.All(3),
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
        };
        box.BindThemedBackgroundColor(s => s.TextInput.Background);
        box.BindThemedBorderColor(s => BorderColorStyle.All(s.TextInput.Border));
        AddChildToSelf(box);

        this.UseBehavior(_ => new ScrollSyncController(_scrollPane, _scrollBar));

        // Start at the min size; the first OnLayoutChildren pass will refine this.
        Height = _minHeight;
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
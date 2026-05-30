using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

public sealed class CheckboxView : HoverableButton
{
    private const float BoxSize = 16f;
    private const float BoxRadius = 3f;
    private const float CheckGlyphSize = 12f;

    public State<bool> IsChecked { get; } = new(false);

    public CheckboxView(string label)
    {
        var labelView = new TextView
        {
            Text = label,
            VerticalTextAlignment = TextAlignment.Center,
        };
        labelView.BindThemedTextColor(SelectForeground);
        Initialize(labelView);
    }

    public CheckboxView(MultiChildView content)
    {
        Initialize(content);
    }

    private void Initialize(MultiChildView content)
    {
        var checkGlyph = new TextView
        {
            FontSize = CheckGlyphSize,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        checkGlyph.BindText(IsChecked, c => c ? "✓" : string.Empty);
        checkGlyph.BindThemedTextColor(s => IsEnabled.Value ? s.Checkbox.CheckGlyph : s.Checkbox.TextDisabled);

        var box = new RectView
        {
            Width = BoxSize,
            Height = BoxSize,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(BoxRadius),
            Children = { checkGlyph },
        };
        box.BindThemedBackgroundColor(SelectBoxFill);
        box.BindThemedBorderColor(s => BorderColorStyle.All(SelectBoxBorder(s)));

        SetBackground(new FlexRowView
        {
            Gap = 8f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { box, new FlexItem { Grow = 1, Child = content } },
        });
    }

    protected override void OnClicked() => IsChecked.Value = !IsChecked.Value;

    private uint SelectForeground(ThemeStyles s)
    {
        if (!IsEnabled.Value) return s.Checkbox.TextDisabled;
        return IsHovered.Value ? s.Checkbox.TextHover : s.Checkbox.TextIdle;
    }

    private uint SelectBoxFill(ThemeStyles s)
    {
        if (!IsEnabled.Value) return IsChecked.Value ? s.Checkbox.BoxFillDisabled : 0x00000000u;
        if (!IsChecked.Value) return 0x00000000u;
        return IsHovered.Value ? s.Checkbox.BoxFillCheckedHover : s.Checkbox.BoxFillChecked;
    }

    private uint SelectBoxBorder(ThemeStyles s)
    {
        if (!IsEnabled.Value) return s.Checkbox.BoxBorderDisabled;
        if (IsChecked.Value) return IsHovered.Value ? s.Checkbox.BoxFillCheckedHover : s.Checkbox.BoxFillChecked;
        return IsHovered.Value ? s.Checkbox.BoxBorderHover : s.Checkbox.BoxBorderIdle;
    }
}

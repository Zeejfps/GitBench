using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Themed checkbox bound two-way to a <see cref="State{T}"/> — clicking toggles the box and
/// writes the state, external writes move the box. Renders a <see cref="Label"/> beside the box,
/// or arbitrary <see cref="Content"/> when a plain label isn't enough. The widget is just state
/// and visuals: it implements <see cref="IInteractable"/> and lets a <see cref="KbmController"/>
/// drive its hover and press, toggling on the rising edge of <see cref="IInteractable.Pressed"/>,
/// so it works the same under any input modality.
/// </summary>
public sealed record Checkbox : Widget, ICheckbox
{
    private const float BoxSize = 16f;
    private const float BoxRadius = 3f;
    private const float CheckGlyphSize = 12f;

    /// <summary>The two-way toggle target: clicking writes it, external writes move the box.</summary>
    public required State<bool> Value { get; init; }

    /// <summary>Text beside the box; ignored when <see cref="Content"/> is set.</summary>
    public Prop<string?> Label { get; init; }

    /// <summary>Arbitrary content beside the box, in place of a plain <see cref="Label"/>.</summary>
    public IWidget? Content { get; init; }

    private readonly State<bool> _enabled = new(true);
    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);

    State<bool> IInteractable.Hovered => _hovered;
    State<bool> IInteractable.Pressed => _pressed;
    State<bool> IInteractable.Enabled => _enabled;
    State<bool> ICheckbox.Checked => Value;

    protected override IWidget Build(Context ctx)
    {
        _pressed.Changed += pressed =>
        {
            if (pressed) Value.Value = !Value.Value;
        };

        return new Row
        {
            Gap = 8f,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Box
                {
                    Width = BoxSize,
                    Height = BoxSize,
                    BorderSize = BorderSizeStyle.All(1),
                    BorderRadius = BorderRadiusStyle.All(BoxRadius),
                    Background = Theme.Color(s => s.Checkbox.BoxFill(this)),
                    BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Checkbox.BoxBorder(this))),
                    Children =
                    [
                        new Text
                        {
                            FontSize = CheckGlyphSize,
                            HAlign = TextAlignment.Center,
                            VAlign = TextAlignment.Center,
                            Value = Value.Bind(string?(c) => c ? "✓" : string.Empty),
                            Color = Theme.Color(s => s.Checkbox.GlyphColor(this)),
                        },
                    ],
                },
                new Grow
                {
                    Child = Content ?? new Text
                    {
                        Value = Label,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Checkbox.Foreground(this)),
                    },
                },
            ],
        };
    }
}

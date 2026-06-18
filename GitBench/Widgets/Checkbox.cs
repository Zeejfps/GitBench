using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>
/// Themed checkbox whose <see cref="Checked"/> input is a two-way <see cref="Prop{T}"/> — clicking
/// toggles the box and writes back through it, external writes move the box. Renders a <see cref="Label"/> beside the box,
/// or arbitrary <see cref="Content"/> when a plain label isn't enough. The widget is just state
/// and visuals: it implements <see cref="IInteractableWidget"/> and lets a <see cref="KbmController"/>
/// drive its hover and press, toggling on the rising edge of <see cref="IInteractableWidget.Pressed"/>,
/// so it works the same under any input modality.
/// </summary>
public sealed record Checkbox : Widget, ICheckbox
{
    private const float BoxSize = 16f;
    private const float BoxRadius = 3f;
    private const float CheckGlyphSize = 12f;

    /// <summary>The two-way toggle target: clicking writes it, external writes move the box.</summary>
    public Prop<bool> Checked { get; init; }

    /// <summary>Text beside the box; ignored when <see cref="Content"/> is set.</summary>
    public Prop<string?> Label { get; init; }

    /// <summary>Arbitrary content beside the box, in place of a plain <see cref="Label"/>.</summary>
    public IWidget? Content { get; init; }

    private readonly State<bool> _enabled = new(true);
    private readonly State<bool> _hovered = new(false);
    private readonly State<bool> _pressed = new(false);
    private IReadable<bool>? _checked;

    IWritable<bool> IInteractableWidget.Hovered => _hovered;
    IWritable<bool> IInteractableWidget.Pressed => _pressed;
    IReadable<bool> IInteractableWidget.Enabled => _enabled;
    IReadable<bool> ICheckbox.Checked => _checked!;

    protected override IWidget Build(Context ctx)
    {
        var checkedValue = Checked.ToReadable(ctx);
        _checked = checkedValue;

        _pressed.Changed += pressed =>
        {
            if (pressed) Checked.Write(!checkedValue.Value);
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
                            Value = checkedValue.Bind(string?(c) => c ? "✓" : string.Empty),
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

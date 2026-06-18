using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Themed checkbox whose <see cref="Checked"/> input is a two-way <see cref="Prop{T}"/> — clicking
/// toggles the box and writes back through it, external writes move the box. Renders a <see cref="Label"/>
/// beside the box, or arbitrary <see cref="Content"/> when a plain label isn't enough. Live state
/// (hover, press, checked) lives on a <see cref="CheckboxState"/> the widget creates in
/// <see cref="CreateState"/> and exposes as its <see cref="IInteractable"/> surface, so the
/// <em>parent</em> attaches a controller (<c>checkbox.WithController&lt;KbmController&gt;()</c>) and an
/// optional tooltip (<c>checkbox.WithTooltip("…")</c>) — it picks the modality, the widget stays neutral.
/// </summary>
public sealed record CheckboxWidget : Widget<CheckboxState>
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

    protected override CheckboxState CreateState(Context ctx) =>
        new(Checked.ToReadable(ctx), Checked.Write);

    protected override IWidget Build(Context ctx, CheckboxState state) =>
        new Row
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
                    Background = Theme.Color(s => s.Checkbox.BoxFill(state)),
                    BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Checkbox.BoxBorder(state))),
                    Children =
                    [
                        new Text
                        {
                            FontSize = CheckGlyphSize,
                            HAlign = TextAlignment.Center,
                            VAlign = TextAlignment.Center,
                            Value = state.Checked.Bind(string?(c) => c ? "✓" : string.Empty),
                            Color = Theme.Color(s => s.Checkbox.GlyphColor(state)),
                        },
                    ],
                },
                new Grow
                {
                    Child = Content ?? new Text
                    {
                        Value = Label,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Checkbox.Foreground(state)),
                    },
                },
            ],
        };
}

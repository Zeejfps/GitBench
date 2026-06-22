using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// The look of a bordered "select" control: caller-supplied row content followed by a chevron, in the
/// outline chrome. It's just the look over a <see cref="ButtonState"/> — the owner bolts on the
/// open-a-menu behavior with <c>dropdown.WithMenuController(rect =&gt; …)</c>. Size it with the inherited
/// <c>Width</c>/<c>Height</c>; pass <see cref="Enabled"/> to gray it out when there's nothing to pick.
/// </summary>
internal sealed record DropdownWidget : Widget<ButtonState>
{
    /// <summary>Row content shown before the chevron.</summary>
    public required IWidget[] Children { get; init; }

    public float Gap { get; init; } = 8f;

    /// <summary>When set and false, the control reads disabled (no hover) — for "nothing to pick".</summary>
    public IReadable<bool>? Enabled { get; init; }

    /// <summary>Whether the trailing chevron shows; hide it when there's nothing to expand.</summary>
    public Prop<bool> ShowChevron { get; init; } = true;

    protected override ButtonState CreateState(Context ctx) => new(enabled: Enabled);

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var content = new IWidget[Children.Length + 1];
        Children.CopyTo(content, 0);
        content[^1] = new Text
        {
            Value = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            Width = 16,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.DialogBody.RowText),
            Visible = ShowChevron,
        };

        return new Box
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Background = Theme.Color(s => s.BorderedButton.Surface(state)),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.BorderedButton.Border(state))),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 8, Right = 8, Top = 4, Bottom = 4 },
                    Children =
                    [
                        new Row { Gap = Gap, CrossAxis = CrossAxisAlignment.Center, Children = content },
                    ],
                },
            ],
        };
    }
}

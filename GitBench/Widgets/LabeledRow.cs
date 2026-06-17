using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// The standard dialog body row: a themed label on the left, the value content growing to
/// fill the rest. Mirrors the BuildLabeledRow helper the legacy dialogs share.
/// </summary>
public sealed record LabeledRow : Widget
{
    public required string Label { get; init; }
    public required IWidget Value { get; init; }
    public float Gap { get; init; } = 10f;

    protected override IWidget Build(Context ctx) => new Row
    {
        Gap = Gap,
        CrossAxis = CrossAxisAlignment.Center,
        Height = 28,
        Children =
        [
            new Text
            {
                Value = Label,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
            },
            new Grow { Child = Value },
        ],
    };
}

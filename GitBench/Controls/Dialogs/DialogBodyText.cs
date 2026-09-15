using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

internal sealed record DialogBodyText : Widget
{
    public required Prop<string?> Value { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = Value,
        Wrap = TextWrap.Wrap,
        Color = Theme.Color(t => t.DialogBody.BodyText),
    };
}

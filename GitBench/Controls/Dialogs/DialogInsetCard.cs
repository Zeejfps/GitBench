using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The sunken surface a dialog sets content down on — a path, a file list, an error transcript.
/// Owns the inset chrome (recessed fill, hairline border, corner radius) and nothing else, so every
/// dialog's inset reads as the same surface and the contents stay the caller's business.
/// </summary>
internal sealed record DialogInsetCard : Widget
{
    public IWidget[] Children { get; init; } = [];

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(t => t.DialogFrame.InsetBackground),
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        BorderColor = Theme.BorderColor(t => BorderColorStyle.All(t.DialogFrame.Border)),
        Children = Children,
    };
}

using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The fixed-width icon a transcript line opens with. One width for every line, so a folded run's
/// calls indent to exactly where the line above them starts.
/// </summary>
internal sealed record TranscriptGlyph : Widget
{
    public const int Box = 14;

    public required Prop<string?> Glyph { get; init; }
    public required Prop<uint> Tint { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = Glyph,
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        Width = Box,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Tint,
    };
}

using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// A tool call as one collapsed line — which tool ran, what it was pointed at, and how it ended.
/// Results stay out of the transcript; the answer that follows is what the reader is here for.
/// </summary>
/// <remarks>
/// The arguments earn their place only as far as the line already goes: one clipped summary from
/// <see cref="ToolCallSummary"/>, dimmer than the tool's own name and elided rather than wrapped, so
/// a reader who wants to know which file was read can see it without the transcript turning into a
/// log of the model's bookkeeping.
/// </remarks>
internal sealed record ToolCallRow : Widget
{
    public required AssistantRow Row { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var row = Row;
        var theme = ctx.Theme();
        var loc = ctx.Localization();
        var name = row.ToolName ?? string.Empty;

        var color = Prop.Bind(() => row.Failed.Value
            ? theme.Styles.Value.Status.DangerText
            : theme.Styles.Value.Palette.TextMuted);

        var glyph = new TranscriptGlyph
        {
            Glyph = Prop.Bind<string?>(() => row.Failed.Value ? LucideIcons.TriangleAlert : LucideIcons.SquareTerminal),
            Tint = color,
        };

        var label = new Text
        {
            Value = Prop.Bind<string?>(() =>
            {
                var strings = loc.Strings.Value;
                if (row.IsRunning.Value) return strings.AssistantToolRunning(name);
                return row.Failed.Value ? strings.AssistantToolFailed(name) : strings.AssistantToolDone(name);
            }),
            FontSize = FontSize.Caption,
            Color = color,
        };

        IWidget[] children = row.ToolDetail is { Length: > 0 } detail
            ? [glyph, label, Separator(), Detail(detail)]
            : [glyph, label];

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children = children,
        };
    }

    private static IWidget Separator() => new Text
    {
        Value = "·",
        FontSize = FontSize.Caption,
        Color = Theme.Color(s => s.Palette.TextDim),
    };

    // Grown and elided rather than sized to the text: a long path shortens instead of widening the
    // transcript, which is the only reason showing arguments here is affordable at all.
    private static IWidget Detail(string detail) => new Grow
    {
        Child = new Text
        {
            Value = detail,
            FontSize = FontSize.Caption,
            HAlign = TextAlignment.Start,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Theme.Color(s => s.Palette.TextDim),
        },
    };
}

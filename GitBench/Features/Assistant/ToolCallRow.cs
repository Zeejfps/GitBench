using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// A tool call as one collapsed line — which tool ran and how it ended. Arguments and results stay
/// out of the transcript; the answer that follows is what the reader is here for.
/// </summary>
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

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new TranscriptGlyph
                {
                    Glyph = Prop.Bind<string?>(() => row.Failed.Value ? LucideIcons.TriangleAlert : LucideIcons.SquareTerminal),
                    Tint = color,
                },
                new Text
                {
                    Value = Prop.Bind<string?>(() =>
                    {
                        var strings = loc.Strings.Value;
                        if (row.IsRunning.Value) return strings.AssistantToolRunning(name);
                        return row.Failed.Value ? strings.AssistantToolFailed(name) : strings.AssistantToolDone(name);
                    }),
                    FontSize = FontSize.Caption,
                    Color = color,
                },
            ],
        };
    }
}

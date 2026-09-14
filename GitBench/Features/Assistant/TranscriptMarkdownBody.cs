using GitBench.Features.Markdown.Rendering;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// A transcript row's text as the markdown the model wrote it in, streamed through
/// <see cref="MarkdownText"/> so a reply keeps its finished blocks' layout while the tail is written.
/// </summary>
internal sealed record TranscriptMarkdownBody : Widget
{
    /// <summary>The row's text, as it grows.</summary>
    public required IReadable<string> Text { get; init; }

    protected override IWidget Build(Context ctx) => new MarkdownText { Text = Text };
}

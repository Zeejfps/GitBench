using GitBench.Features.Markdown.Parsing;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// A bound string rendered as the markdown it is written in — headings, lists, tables, links and
/// highlighted code blocks instead of the asterisks and pipes they are spelled with.
/// </summary>
/// <remarks>
/// The text may be a stream, so the document is fed through <see cref="MarkdownBlockList"/> rather
/// than re-parsed into a fresh widget: deltas coalesce to one parse per frame, and every block the
/// delta left alone keeps its view and its wrapped layout while the text goes on being written.
/// The text already held on mount is applied at once instead — a finished document is not a delta,
/// and waiting a frame for it would show a blank row first.
/// </remarks>
internal sealed record MarkdownText : Widget<MarkdownText.Body>
{
    /// <summary>The text, as it grows.</summary>
    public required IReadable<string> Text { get; init; }

    protected override Body CreateState(Context ctx) =>
        new(new BasicMarkdownParser(), ctx.Require<IFrameTicker>(), Text);

    protected override IWidget Build(Context ctx, Body body) => new MarkdownStream { Source = body.Blocks };

    /// <summary>Keeps one parsed block list in step with the text it was given, for as long as the
    /// widget's view lives.</summary>
    internal sealed class Body : IDisposable
    {
        private readonly IDisposable _subscription;
        private bool _seeded;

        public Body(IMarkdownParser parser, IFrameTicker ticker, IReadable<string> text)
        {
            Blocks = new MarkdownBlockList(parser, ticker);
            _subscription = text.Subscribe(value =>
            {
                if (_seeded)
                {
                    Blocks.SetTextThrottled(value);
                    return;
                }

                _seeded = true;
                Blocks.SetText(value);
            });
        }

        public MarkdownBlockList Blocks { get; }

        public void Dispose()
        {
            _subscription.Dispose();
            Blocks.Dispose();
        }
    }
}

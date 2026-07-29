using GitBench.Features.Assistant;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Tests;

// The model answers in markdown, so a reply reads as one: headings and emphasis are rendered rather
// than spelled out. What is pinned here is the integration itself — the row feeds the streaming
// renderer, so a delta costs one parse on the next frame and leaves the blocks it did not touch
// (their views, their wrapped layout) alone — and the copy that stands in for the selection a
// laid-out document cannot offer.
public sealed class AssistantTranscriptMarkdownTests
{
    private const float Frame = 1f / 30f;

    [Fact]
    public void AReplyRendersMarkdownRatherThanItsSource()
    {
        using var reply = Mount("## Findings\n\nthe **fix** landed");

        var canvas = reply.Harness.Render();

        Assert.True(Drew(canvas, "Findings"), "the heading text never reached the canvas");
        Assert.True(Drew(canvas, "fix"), "the emphasized text never reached the canvas");
        Assert.False(Drew(canvas, "##"), "the heading marker was drawn as text");
        Assert.False(Drew(canvas, "**"), "the emphasis markers were drawn as text");
    }

    [Fact]
    public void StreamedTextReachesTheRenderOnTheNextFrame()
    {
        using var reply = Mount();

        reply.Row.Append("a streamed answer");
        reply.Harness.Tick(Frame);
        var canvas = reply.Harness.Render();

        Assert.True(Drew(canvas, "a streamed answer"));
    }

    // The point of feeding MarkdownBlockList rather than reparsing into a fresh widget per delta:
    // the paragraph that is already finished keeps the view it was laid out in while the tail grows.
    [Fact]
    public void ACompletedBlockKeepsItsViewWhileTheAnswerGrows()
    {
        using var reply = Mount();

        reply.Row.Append("intro paragraph\n\ntail");
        reply.Harness.Tick(Frame);
        reply.Harness.Render();
        var intro = reply.RichTextViews[0];

        reply.Row.Append(" keeps coming");
        reply.Harness.Tick(Frame);
        var canvas = reply.Harness.Render();

        Assert.Same(intro, reply.RichTextViews[0]);
        Assert.True(Drew(canvas, "tail keeps coming"));
    }

    [Fact]
    public void TheCopyButtonTakesTheMarkdownSource()
    {
        const string source = "# Title\n\nbody **bold**";
        using var reply = Mount(source);
        reply.Harness.Render();

        reply.Harness.Click(reply.Localization.Strings.Value.CommonCopy);

        Assert.Equal(source, reply.Clipboard.Text);
    }

    // A copy taken mid-answer is the answer as it stands, not as it was when the row was built.
    [Fact]
    public void TheCopyButtonFollowsAStreamingAnswer()
    {
        using var reply = Mount("first line");
        reply.Harness.Render();

        reply.Row.Append("\n\nsecond line");
        reply.Harness.Tick(Frame);
        reply.Harness.Render();
        reply.Harness.Click(reply.Localization.Strings.Value.CommonCopy);

        Assert.Equal("first line\n\nsecond line", reply.Clipboard.Text);
    }

    private static bool Drew(RecordingCanvas canvas, string fragment) =>
        canvas.Texts.Any(t => t.Inputs.Text.Contains(fragment, StringComparison.Ordinal));

    private static MountedReply Mount(string text = "")
    {
        var row = AssistantRow.Reply();
        if (text.Length > 0) row.Append(text);

        var clipboard = new FakeClipboard();
        var localization = new LocalizationService(new State<Locale>(Locale.En));
        var harness = GuiTestHarness.Create(
            ctx => new Column
            {
                MainAxis = MainAxisAlignment.Start,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [new TranscriptReplyRow { Row = row }],
            }.BuildView(ctx),
            width: 420,
            height: 300,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(localization);
                ctx.AddService<IClipboard>(clipboard);
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
            });
        harness.Layout();

        return new MountedReply(harness, clipboard, localization, row);
    }

    private sealed class MountedReply : IDisposable
    {
        public MountedReply(
            GuiTestHarness harness,
            FakeClipboard clipboard,
            ILocalizationService localization,
            AssistantRow row)
        {
            Harness = harness;
            Clipboard = clipboard;
            Localization = localization;
            Row = row;
        }

        public GuiTestHarness Harness { get; }
        public FakeClipboard Clipboard { get; }
        public ILocalizationService Localization { get; }
        public AssistantRow Row { get; }

        /// <summary>The rendered blocks' text views, in document order — the identity probe for a
        /// block whose view must survive the next delta.</summary>
        public List<RichTextView> RichTextViews =>
            Harness.Root.SelfAndDescendants().OfType<RichTextView>().ToList();

        public void Dispose()
        {
            Harness.Dispose();
            Row.Dispose();
        }
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }
}

using GitBench.Features.Assistant;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Tests;

// A model reply is rendered markdown, not a text field — and the reader still has to be able to drag
// across it and copy what they read. Pinned here is that behaviour where it actually lives, in the
// transcript: a drag inside one reply copies its rendered text (no '#', no '**'), a drag that
// wanders into the next reply stays in the one it started in, and a streaming delta that re-parses
// the block a selection is anchored in takes the selection with it.
//
// The per-reply copy button is untouched by all of this: it copies the markdown SOURCE, and
// AssistantTranscriptMarkdownTests pins that.
public sealed class AssistantTranscriptSelectionTests
{
    private const float Frame = 1f / 30f;
    private const float Advance = 8f;
    private const float LineH = 16f;

    [Fact]
    public void DraggingAcrossAReplyCopiesItsRenderedText()
    {
        using var t = Mount("## Findings\n\nthe **fix** landed");
        var leaves = t.Leaves;
        Assert.Equal(2, leaves.Count);

        Drag(t.Harness, At(leaves[0], 0), At(leaves[1], "the fix landed".Length));
        t.Harness.PressKey(KeyboardKey.C, InputModifiers.Control);

        Assert.Equal("Findings\nthe fix landed", t.Clipboard.Text);
    }

    // One reply is one markdown surface, so a selection cannot reach past it. A drag that runs off
    // the bottom of the answer it started in selects to that answer's end and stops there.
    [Fact]
    public void ADragThatWandersIntoTheNextReplySelectsOnlyTheOneItStartedIn()
    {
        using var t = Mount("reply one text", "reply two text");
        var leaves = t.Leaves;
        Assert.Equal(2, leaves.Count);

        Drag(t.Harness, At(leaves[0], 0), At(leaves[1], "reply two text".Length));
        t.Harness.PressKey(KeyboardKey.C, InputModifiers.Control);

        Assert.Equal("reply one text", t.Clipboard.Text);
    }

    // The answer is still being written: the delta re-parses the block the selection is anchored in,
    // that block's view is rebuilt, and a highlight over text that has moved would be a lie.
    [Fact]
    public void AStreamedDeltaThatReparsesTheAnchoringBlockClearsTheSelection()
    {
        using var t = Mount("intro paragraph\n\ntail");
        var tail = t.Leaves[1];

        Drag(t.Harness, At(tail, 0), At(tail, 4));
        Assert.NotEmpty(SelectionRects(t.Harness.Render()));

        t.Rows[0].Append(" keeps coming");
        t.Harness.Tick(Frame);
        t.Harness.Render();

        Assert.Empty(SelectionRects(t.Harness.Render()));
        t.Harness.PressKey(KeyboardKey.C, InputModifiers.Control);
        Assert.Null(t.Clipboard.Text);
    }

    // ---------------------------------------------------------------- harness

    private static PointF At(RichTextView leaf, int charOffset, int line = 0) =>
        new(leaf.Position.Left + charOffset * Advance, leaf.Position.Top - line * LineH - LineH / 2f);

    private static void Drag(GuiTestHarness h, PointF from, PointF to)
    {
        h.MoveTo(from.X, from.Y);
        h.Press();
        h.MoveTo(to.X, to.Y);
        h.Release();
    }

    private static IReadOnlyList<RectF> SelectionRects(RecordingCanvas canvas)
    {
        var color = ThemeStyles.Dark.Markdown.SelectionBackground;
        var rects = new List<RectF>();
        foreach (var r in canvas.Rects)
            if (r.Inputs.Style.BackgroundColor == color)
                rects.Add(r.Inputs.Position);
        return rects;
    }

    private static MountedTranscript Mount(params string[] replies)
    {
        var rows = replies.Select(text =>
        {
            var row = AssistantRow.Reply();
            row.Append(text);
            return row;
        }).ToList();

        var clipboard = new FakeClipboard();
        var localization = new LocalizationService(new State<Locale>(Locale.En));
        var harness = GuiTestHarness.Create(
            ctx => new Column
            {
                Gap = 8,
                MainAxis = MainAxisAlignment.Start,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = rows.Select(IWidget (r) => new TranscriptReplyRow { Row = r }).ToArray(),
            }.BuildView(ctx),
            width: 420,
            height: 500,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(localization);
                ctx.AddService<IClipboard>(clipboard);
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
            });
        harness.Render();

        return new MountedTranscript(harness, clipboard, localization, rows);
    }

    private sealed class MountedTranscript : IDisposable
    {
        public MountedTranscript(
            GuiTestHarness harness,
            FakeClipboard clipboard,
            ILocalizationService localization,
            IReadOnlyList<AssistantRow> rows)
        {
            Harness = harness;
            Clipboard = clipboard;
            Localization = localization;
            Rows = rows;
        }

        public GuiTestHarness Harness { get; }
        public FakeClipboard Clipboard { get; }
        public ILocalizationService Localization { get; }
        public IReadOnlyList<AssistantRow> Rows { get; }

        /// <summary>Every reply's text leaves, in geometric document order (top-down, then
        /// left-to-right — GUI coordinates are y-up).</summary>
        public List<RichTextView> Leaves =>
            Harness.Root.SelfAndDescendants().OfType<RichTextView>()
                .OrderByDescending(v => v.Position.Top)
                .ThenBy(v => v.Position.Left)
                .ToList();

        public void Dispose()
        {
            Harness.Dispose();
            foreach (var row in Rows) row.Dispose();
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

using GitBench.Features.Markdown.Rendering;
using GitBench.Platform;
using Xunit;
using ZGF.Desktop;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;

namespace GitBench.Tests.Markdown;

// Input tests for LinkController in the DiffSelectionViewTests style: real dispatch through the
// harness's InputSystem over a real RichTextView, synthetic geometry (8px advance, 16px lines,
// 600px-tall viewport → first line's y-center is 592). Clicking a link segment must open its
// url through IPlatformShell.OpenUrl; hover must show the hand cursor (IProvidesCursor, read
// off InputSystem.DesiredCursor) and recolor the link via RichTextView.SetHoveredLink.
public class LinkControllerTests
{
    private const uint PlainColor = 0xFF111111;
    private const uint LinkColor = 0xFF3366FF;
    private const uint HoverColor = 0xFFFF8800;
    private const string Url = "https://example.com/docs";
    private const float FirstLineY = 592f;

    private sealed class FakeShell : IPlatformShell
    {
        public readonly List<string> OpenedUrls = new();
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) => OpenedUrls.Add(url);
    }

    private static TextStyle Style(uint color) => new() { TextColor = color };

    private static RichTextRun Run(string text) => new(text, Style(PlainColor));

    private static RichTextRun Link(string text, string url = Url) =>
        new(text, Style(LinkColor), Underline: true, LinkUrl: url);

    private static (GuiTestHarness Harness, RichTextView View, FakeShell Shell) Create(
        IReadOnlyList<RichTextRun> runs, int width = 800)
    {
        var shell = new FakeShell();
        RichTextView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new RichTextView(ctx.Canvas) { Runs = runs, LinkHoverColor = HoverColor };
                view.UseController(ctx.Require<InputSystem>(), () => new LinkController(view, shell));
                return view;
            },
            width, 600);
        return (harness, view, shell);
    }

    // "Read the " spans x [0,72), the link "docs" [72,104), " now." [104,144).
    private static IReadOnlyList<RichTextRun> Sample() =>
        new[] { Run("Read the "), Link("docs"), Run(" now.") };

    [Fact]
    public void ClickOnALinkSegmentOpensItsUrl()
    {
        var (h, _, shell) = Create(Sample());
        using (h)
        {
            h.Click(80f, FirstLineY);

            Assert.Equal(new[] { Url }, shell.OpenedUrls);
        }
    }

    [Fact]
    public void ClickOnPlainTextOpensNothing()
    {
        var (h, _, shell) = Create(Sample());
        using (h)
        {
            h.Click(30f, FirstLineY);

            Assert.Empty(shell.OpenedUrls);
        }
    }

    [Fact]
    public void ClickBeyondTheLineOpensNothing()
    {
        var (h, _, shell) = Create(Sample());
        using (h)
        {
            h.Click(300f, FirstLineY);

            Assert.Empty(shell.OpenedUrls);
        }
    }

    [Fact]
    public void EachLinkOpensItsOwnUrl()
    {
        // "a" [0,8), " mid " [8,48), "bb" [48,64).
        var runs = new[] { Link("a", "https://one.example"), Run(" mid "), Link("bb", "https://two.example") };
        var (h, _, shell) = Create(runs);
        using (h)
        {
            h.Click(56f, FirstLineY);

            Assert.Equal(new[] { "https://two.example" }, shell.OpenedUrls);
        }
    }

    [Fact]
    public void WrappedLinkIsClickableOnItsSecondLine()
    {
        // At 48px "aaaa bbbb" wraps to "aaaa " / "bbbb"; line 2's band is y [568,584).
        var (h, _, shell) = Create(new[] { Link("aaaa bbbb") }, width: 48);
        using (h)
        {
            h.Click(10f, 576f);

            Assert.Equal(new[] { Url }, shell.OpenedUrls);
        }
    }

    [Fact]
    public void HoveringALinkShowsTheHandCursor()
    {
        var (h, _, _) = Create(Sample());
        using (h)
        {
            h.MoveTo(80f, FirstLineY);

            Assert.Equal(MouseCursor.Hand, h.Input.DesiredCursor);
        }
    }

    [Fact]
    public void MovingOffTheLinkRestoresTheDefaultCursor()
    {
        var (h, _, _) = Create(Sample());
        using (h)
        {
            h.MoveTo(80f, FirstLineY);
            h.MoveTo(30f, FirstLineY);

            Assert.Equal(MouseCursor.Default, h.Input.DesiredCursor);
        }
    }

    [Fact]
    public void HoveringRecolorsTheLinkAndLeavingRestoresIt()
    {
        var (h, _, _) = Create(Sample());
        using (h)
        {
            h.MoveTo(80f, FirstLineY);
            var canvas = h.Render();
            Assert.Equal(HoverColor, canvas.Texts.Single(t => t.Inputs.Text == "docs").Inputs.Style.TextColor.Value);

            h.MoveTo(30f, FirstLineY);
            canvas = h.Render();
            Assert.Equal(LinkColor, canvas.Texts.Single(t => t.Inputs.Text == "docs").Inputs.Style.TextColor.Value);
        }
    }

    // The RichText widget must wire the controller itself: building it in a context that has an
    // IPlatformShell yields clickable links with no extra call-site ceremony.
    [Fact]
    public void RichTextWidgetAttachesTheLinkController()
    {
        var shell = new FakeShell();
        var harness = GuiTestHarness.Create(
            ctx => new RichText
            {
                // Interface-typed props don't get the implicit T → Prop<T> conversion (C# skips
                // user-defined conversions involving interfaces), so the ctor is spelled out.
                Runs = new(Sample()),
                CodeChipBackground = 0u,
                LinkHoverColor = HoverColor,
            }.BuildView(ctx),
            800, 600,
            configure: ctx => ctx.AddService<IPlatformShell>(shell));
        using (harness)
        {
            harness.Click(80f, FirstLineY);

            Assert.Equal(new[] { Url }, shell.OpenedUrls);
        }
    }
}

using GitBench.Features.Terminal;
using GitBench.Platform;
using GitBench.Terminal.Vt;
using ZGF.Desktop;
using ZGF.Gui.Desktop.Input;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The boundary between a url a program sent through OSC 8 and one this application will open.
/// </summary>
/// <remarks>
/// Pure, and deliberately so: this is the whole of the policy, it needs no window to state, and it
/// stays correct when a second entry point appears — a context menu, a status strip — that would
/// otherwise each need their own copy of the scheme check.
/// </remarks>
public class TerminalLinkTargetTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a?b=c#d", true)]
    // Legal in a url and legal in an OSC 8 payload: the separator is the first ';' only.
    [InlineData("https://example.com/a;b", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData(@"\\evil.example\share\run.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("/relative/path", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void OnlyAbsoluteHttpLinksSurviveTheBoundary(string url, bool opens) =>
        Assert.Equal(opens, TerminalLinkTarget.FromProgram(new TerminalHyperlink(url)) is not null);

    [Fact]
    public void TheUrlIsKeptAsTheProgramWroteIt()
    {
        var target = TerminalLinkTarget.FromProgram(new TerminalHyperlink("https://example.com/a%2Fb"));

        Assert.NotNull(target);
        Assert.Equal("https://example.com/a%2Fb", target.Text);
    }
}

/// <summary>
/// Following a hyperlink with the pointer: what shows a hand, what opens, and — mostly — what must
/// not happen at the same time.
/// </summary>
/// <remarks>
/// The interesting cases are all collisions. A terminal pane's pointer already means three things —
/// selecting, reporting to a program, and now following a link — and the gesture is one value
/// precisely so a press cannot be two of them at once. Each test below is a pair that a flag beside
/// the gesture would let both happen.
/// </remarks>
public class TerminalLinkGestureTests
{
    const string Url = "https://example.com/docs";

    static TerminalLinkTarget Target =>
        TerminalLinkTarget.FromProgram(new TerminalHyperlink(Url))!;

    static DragPane PaneOverALink(DragPane pane)
    {
        pane.Cells.Link = Target;
        return pane;
    }

    [Fact]
    public void ModifiedClickOnALink_OpensIt()
    {
        using var pane = PaneOverALink(DragPane.Create());

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.ReleaseAt(2, 0, InputModifiers.Super);

        Assert.Equal([Url], pane.Shell.OpenedUrls);
    }

    /// <remarks>
    /// The collision a link held in a field beside the gesture produces: the release would run the
    /// armed-press branch, which clears the selection, as well as opening the link.
    /// </remarks>
    [Fact]
    public void ModifiedClickOnALink_DoesNotAlsoClearTheSelection()
    {
        using var pane = PaneOverALink(DragPane.Create());
        pane.PressAt(0, 0);
        pane.MoveTo(4, 0);
        pane.ReleaseAt(4, 0);
        Assert.NotNull(pane.Terminal.Selection);

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.ReleaseAt(2, 0, InputModifiers.Super);

        Assert.Equal([Url], pane.Shell.OpenedUrls);
        Assert.NotNull(pane.Terminal.Selection);
    }

    /// <remarks>The other half of the same collision: the program must not also see the click.</remarks>
    [Fact]
    public void ModifiedClickOnALink_IsNotAlsoReportedToAProgramTrackingTheMouse()
    {
        using var pane = PaneOverALink(DragPane.Tracking());

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.ReleaseAt(2, 0, InputModifiers.Super);

        Assert.Equal([Url], pane.Shell.OpenedUrls);
        Assert.Empty(pane.Terminal.Sent);
    }

    /// <remarks>
    /// Travel cancels rather than becoming a selection. It reads like the markdown link
    /// controller's rule and is not the same one: there travel means the reader was highlighting the
    /// text, while here the modifier is held down, so travel means they changed their mind.
    /// </remarks>
    [Fact]
    public void ModifiedDragOffALink_OpensNothingAndSelectsNothing()
    {
        using var pane = PaneOverALink(DragPane.Create());

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.MoveTo(9, 0);
        pane.ReleaseAt(9, 0, InputModifiers.Super);

        Assert.Empty(pane.Shell.OpenedUrls);
        Assert.Null(pane.Terminal.Selection);
    }

    [Fact]
    public void BareClickOnALink_OpensNothing()
    {
        using var pane = PaneOverALink(DragPane.Create());

        pane.PressAt(2, 0);
        pane.ReleaseAt(2, 0);

        Assert.Empty(pane.Shell.OpenedUrls);
    }

    /// <remarks>
    /// The screen can scroll between the press and the release, so what was under the pointer is not
    /// necessarily what is under it now. Opening the first would open something nobody aimed at.
    /// </remarks>
    [Fact]
    public void AReleaseOverADifferentLink_OpensNeither()
    {
        using var pane = PaneOverALink(DragPane.Create());

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.Cells.Link = TerminalLinkTarget.FromProgram(new TerminalHyperlink("https://elsewhere.example"));
        pane.ReleaseAt(2, 0, InputModifiers.Super);

        Assert.Empty(pane.Shell.OpenedUrls);
    }

    [Fact]
    public void AReleaseAfterTheLinkHasGone_OpensNothing()
    {
        using var pane = PaneOverALink(DragPane.Create());

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.Cells.Link = null;
        pane.ReleaseAt(2, 0, InputModifiers.Super);

        Assert.Empty(pane.Shell.OpenedUrls);
    }

    [Fact]
    public void ModifiedClickOnPlainText_SelectsAsItAlwaysDid()
    {
        using var pane = DragPane.Create();

        pane.PressAt(0, 0, InputModifiers.Super);
        pane.MoveTo(4, 0);
        pane.ReleaseAt(4, 0, InputModifiers.Super);

        Assert.Empty(pane.Shell.OpenedUrls);
        Assert.NotNull(pane.Terminal.Selection);
    }
}

/// <summary>
/// The hand cursor, and the hover state behind it.
/// </summary>
/// <remarks>
/// Every case here is really the same one: nothing about a hover is remembered. The cursor asks the
/// geometry each time it is read, because the cell under a pointer that has not moved changes
/// whenever the shell prints — and no pointer event is delivered when it does.
/// </remarks>
public class TerminalLinkHoverTests
{
    static TerminalLinkTarget Target =>
        TerminalLinkTarget.FromProgram(new TerminalHyperlink("https://example.com/docs"))!;

    [Fact]
    public void OverALink_ThePointerIsAHand()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = Target;

        pane.HoverAt(2, 0);

        Assert.Equal(MouseCursor.Hand, pane.Controller.Cursor);
    }

    [Fact]
    public void OverPlainText_ThePointerIsTheDefault()
    {
        using var pane = DragPane.Create();

        pane.HoverAt(2, 0);

        Assert.Equal(MouseCursor.Default, pane.Controller.Cursor);
    }

    /// <remarks>
    /// The failure a remembered link produces, and the reason the hover keeps a point rather than an
    /// id: the pointer has not moved, so nothing tells the controller anything changed.
    /// </remarks>
    [Fact]
    public void WhenTheLinkScrollsOutFromUnderAStillPointer_TheHandGoes()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = Target;
        pane.HoverAt(2, 0);
        Assert.Equal(MouseCursor.Hand, pane.Controller.Cursor);

        pane.Cells.Link = null;

        Assert.Equal(MouseCursor.Default, pane.Controller.Cursor);
    }

    [Fact]
    public void ALinkThisApplicationWillNotOpen_GetsNoAffordance()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = TerminalLinkTarget.FromProgram(new TerminalHyperlink("file:///etc/passwd"));

        pane.HoverAt(2, 0);

        Assert.Null(pane.Cells.Link);
        Assert.Equal(MouseCursor.Default, pane.Controller.Cursor);
    }

    /// <remarks>
    /// The press is the commit, so the affordance has to survive it — the underline going out under
    /// the pointer that is about to follow the link is the one frame the user is looking at it.
    /// </remarks>
    [Fact]
    public void WhileALinkPressIsHeld_TheHoverStays()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = Target;
        pane.HoverAt(2, 0);

        pane.PressAt(2, 0, InputModifiers.Super);
        pane.HoverAt(2, 0);

        Assert.NotNull(pane.Cells.HoverPoint);
        Assert.Equal(MouseCursor.Hand, pane.Controller.Cursor);
    }

    [Fact]
    public void HoveringTellsTheViewWhereThePointerIs()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = Target;

        pane.HoverAt(2, 0);

        Assert.NotNull(pane.Cells.HoverPoint);
    }

    /// <remarks>
    /// A hover frozen for the length of a drag is the same stale-pointer bug arriving through a
    /// different door, so it is cleared rather than held.
    /// </remarks>
    [Fact]
    public void DuringASelectionDrag_ThereIsNoHover()
    {
        using var pane = DragPane.Create();
        pane.Cells.Link = Target;
        pane.HoverAt(2, 0);

        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        Assert.Null(pane.Cells.HoverPoint);
        Assert.Equal(MouseCursor.Default, pane.Controller.Cursor);
    }
}

internal sealed class RecordingShell : IPlatformShell
{
    public List<string> OpenedUrls { get; } = [];

    public Exception? OpenUrlThrows { get; set; }

    public void OpenFolder(string path) { }

    public void OpenTerminal(string path) { }

    public void OpenFile(string path) { }

    public void OpenUrl(string url)
    {
        OpenedUrls.Add(url);
        if (OpenUrlThrows is { } e) throw e;
    }
}

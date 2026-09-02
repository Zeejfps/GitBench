using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The question asked before a paste of more than one line reaches a shell that will run each of
/// them.
/// </summary>
/// <remarks>
/// Driven through the pane rather than the encoder, because the decision being pinned is not how the
/// text is counted but whether anything was sent before the sender answered.
/// </remarks>
public class TerminalPasteGuardTests
{
    [Fact]
    public void AMultiLinePaste_AsksBeforeSendingAnything()
    {
        using var pane = DragPane.Create();
        pane.Clipboard.Text = "one\ntwo\nthree";

        Paste(pane);

        Assert.Single(pane.Dialogs);
        Assert.Equal(string.Empty, pane.Terminal.Pasted);
    }

    [Fact]
    public void ASingleLinePaste_GoesStraightThrough()
    {
        using var pane = DragPane.Create();
        pane.Clipboard.Text = "git status";

        Paste(pane);

        Assert.Empty(pane.Dialogs);
        Assert.Equal("git status", pane.Terminal.Pasted);
    }

    /// <remarks>
    /// One trailing newline is the sender running the one command they copied, not a list. Stopping
    /// to ask about that would be a terminal nobody pastes into twice.
    /// </remarks>
    [Fact]
    public void ASingleLineEndingInANewline_GoesStraightThrough()
    {
        using var pane = DragPane.Create();
        pane.Clipboard.Text = "git status\r\n";

        Paste(pane);

        Assert.Empty(pane.Dialogs);
        Assert.Equal("git status\r\n", pane.Terminal.Pasted);
    }

    /// <remarks>
    /// The program has said it will take the text as text, so the line endings are characters and
    /// there is nothing to ask about.
    /// </remarks>
    [Fact]
    public void UnderBracketedPaste_AMultiLinePasteIsNotWorthAsking()
    {
        using var pane = DragPane.Bracketing();
        pane.Clipboard.Text = "one\ntwo\nthree";

        Paste(pane);

        Assert.Empty(pane.Dialogs);
        Assert.Equal("one\ntwo\nthree", pane.Terminal.Pasted);
    }

    /// <remarks>
    /// The pane is wired without a bus in most of the suite and in any host that has not registered
    /// one. It must still paste rather than swallowing the text into a question nobody can answer.
    /// </remarks>
    [Fact]
    public void WithNowhereToAsk_TheTextIsSentRatherThanLost()
    {
        using var pane = DragPane.Unhosted();
        pane.Clipboard.Text = "one\ntwo";

        Paste(pane);

        Assert.Equal("one\ntwo", pane.Terminal.Pasted);
    }

    static void Paste(DragPane pane)
    {
        pane.RightPressAt(2, 0);
        pane.Harness.ClickMenuItem("Paste");
        Assert.Equal(0, pane.Harness.OpenMenuCount);
        pane.Settle();
    }
}

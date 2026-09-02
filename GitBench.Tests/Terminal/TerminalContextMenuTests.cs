using GitBench.Features.Terminal;
using ZGF.Gui.Desktop.Input;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The pane's right-click menu: when it is the pane's click to take, and what the menu does.
/// </summary>
/// <remarks>
/// Driven through the headless context-menu host, so what is asserted is the menu a real right-click
/// builds — its rows, the chords printed beside them, and what clicking one actually runs.
/// </remarks>
public class TerminalContextMenuTests
{
    static string CopyChord => OperatingSystem.IsMacOS() ? "⌘C" : "Ctrl+Shift+C";

    [Fact]
    public void ARightClick_OpensTheClipboardMenu()
    {
        using var pane = DragPane.Create();

        pane.RightPressAt(2, 0);

        Assert.Equal(1, pane.Harness.OpenMenuCount);
        var menu = pane.Harness.SnapshotWindows().ToText();
        Assert.Contains("Copy", menu);
        Assert.Contains("Paste", menu);
        Assert.Contains("Select All", menu);
    }

    /// <remarks>
    /// The hints come from the same gestures the keyboard dispatches on, so this is the test that
    /// says the menu cannot print a chord the pane does not answer to.
    /// </remarks>
    [Fact]
    public void TheMenu_PrintsTheChordThatRunsTheSameAction()
    {
        using var pane = DragPane.Create();

        pane.RightPressAt(2, 0);

        Assert.Contains(CopyChord, pane.Harness.SnapshotWindows().ToText());
    }

    /// <remarks>
    /// A right-click is real input to vim and tmux. A menu that appeared over them instead would be
    /// the pane breaking the program it is running, which is the same rule the selection follows.
    /// </remarks>
    [Fact]
    public void WhileAProgramReadsTheMouse_ARightClickIsItsInputRatherThanTheMenu()
    {
        using var pane = DragPane.Tracking();

        pane.RightPressAt(2, 0);

        Assert.Equal(0, pane.Harness.OpenMenuCount);
        Assert.NotEmpty(pane.Terminal.Sent);
    }

    [Fact]
    public void ShiftTakesTheMenuBack_FromAProgramReadingTheMouse()
    {
        using var pane = DragPane.Tracking();

        pane.RightPressAt(2, 0, InputModifiers.Shift);

        Assert.Equal(1, pane.Harness.OpenMenuCount);
        Assert.Empty(pane.Terminal.Sent);
    }

    [Fact]
    public void Copy_PutsTheSelectionOnTheClipboard()
    {
        using var pane = DragPane.Create();
        pane.Terminal.SelectionTextValue = "hello";
        pane.RightPressAt(2, 0);

        pane.Harness.ClickMenuItem("Copy");

        // The menu closes before the action it chose runs, so the count is read first — reading it
        // is what applies the close the click asked for.
        Assert.Equal(0, pane.Harness.OpenMenuCount);
        Assert.Equal("hello", pane.Clipboard.Text);
    }

    /// <remarks>
    /// Present and disabled rather than absent: a menu whose rows move under the pointer between two
    /// right-clicks is one nobody can build a habit on.
    /// </remarks>
    [Fact]
    public void WithNothingSelected_CopyIsThereButDoesNothing()
    {
        using var pane = DragPane.Create();
        pane.RightPressAt(2, 0);

        pane.Harness.ClickMenuItem("Copy");

        Assert.Null(pane.Clipboard.Text);
        Assert.Equal(1, pane.Harness.OpenMenuCount);
    }

    [Fact]
    public void Paste_SendsTheClipboardToTheShell()
    {
        using var pane = DragPane.Create();
        pane.Clipboard.Text = "pasted";
        pane.RightPressAt(2, 0);

        pane.Harness.ClickMenuItem("Paste");

        Assert.Equal(0, pane.Harness.OpenMenuCount);
        Assert.Equal("pasted", pane.Terminal.Pasted);
    }

    [Fact]
    public void WithAnEmptyClipboard_PasteIsThereButDoesNothing()
    {
        using var pane = DragPane.Create();
        pane.RightPressAt(2, 0);

        pane.Harness.ClickMenuItem("Paste");

        Assert.Equal(1, pane.Harness.OpenMenuCount);
        Assert.Equal(string.Empty, pane.Terminal.Pasted);
    }

    [Fact]
    public void SelectAll_HighlightsTheWholeBufferIncludingTheHistory()
    {
        using var pane = DragPane.Create();
        pane.RightPressAt(2, 0);

        pane.Harness.ClickMenuItem("Select All");

        Assert.Equal(0, pane.Harness.OpenMenuCount);
        Assert.Equal(new GridPoint(0, -1000), pane.Terminal.Selection?.Start);
    }

    /// <remarks>
    /// The screen a finished command left is what a reader most wants to copy, so the menu outlives
    /// the shell — with only the row that needs one to run turned off.
    /// </remarks>
    [Fact]
    public void AfterTheShellExits_TheMenuStillOpens_WithPasteTurnedOff()
    {
        using var pane = DragPane.Exited();
        pane.Clipboard.Text = "pasted";
        pane.RightPressAt(2, 0);

        Assert.Equal(1, pane.Harness.OpenMenuCount);

        pane.Harness.ClickMenuItem("Paste");

        Assert.Equal(1, pane.Harness.OpenMenuCount);
        Assert.Equal(string.Empty, pane.Terminal.Pasted);
    }
}

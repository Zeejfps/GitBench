using GitBench.Infrastructure;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>Fixture plumbing shared by the <see cref="EditSession"/> suites.</summary>
internal static class EditorSession
{
    public static EditSession Of(string text, EditOptions? options = null) =>
        new(TextDocument.FromText(text), options ?? EditOptions.Default);

    public static EditOptions Options(
        IndentStyle indent = IndentStyle.Spaces,
        LineEnding eol = LineEnding.Lf,
        string? lineComment = null) =>
        new(indent, eol, lineComment);

    public static SelectionRange Caret(int line, int column) =>
        SelectionRange.At(TextPosition.At(line, column));

    public static SelectionRange Select(int anchorLine, int anchorColumn, int caretLine, int caretColumn) =>
        new(TextPosition.At(anchorLine, anchorColumn), TextPosition.At(caretLine, caretColumn));
}

/// <summary>The ends of the document, which is where every off-by-one in an editor shows up first.</summary>
public sealed class EditorSessionTests
{
    [Fact]
    public void BackspaceAtTheStartOfTheDocumentRecordsNothing()
    {
        var session = EditorSession.Of("abc");

        var after = session.Delete(EditorSession.Caret(1, 0), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("abc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 0), after.Caret);
        Assert.False(session.Journal.CanUndo);
        Assert.Equal(0, session.Document.Revision);
    }

    [Fact]
    public void ForwardDeleteAtTheEndOfTheDocumentRecordsNothing()
    {
        var session = EditorSession.Of("abc\nde");

        var after = session.Delete(EditorSession.Caret(2, 2), TextUnit.Cluster, MoveDirection.Forward);

        Assert.Equal("abc\nde", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 2), after.Caret);
        Assert.False(session.Journal.CanUndo);
    }

    [Fact]
    public void WordDeleteAtTheEndsOfTheDocumentRecordsNothing()
    {
        var session = EditorSession.Of("abc");

        Assert.False(session.Journal.CanUndo);
        session.Delete(EditorSession.Caret(1, 0), TextUnit.Word, MoveDirection.Backward);
        session.Delete(EditorSession.Caret(1, 3), TextUnit.Word, MoveDirection.Forward);

        Assert.Equal("abc", session.Document.Text);
        Assert.False(session.Journal.CanUndo);
    }

    [Fact]
    public void MotionPastEitherEndOfTheDocumentStaysPut()
    {
        var session = EditorSession.Of("ab\ncd");

        Assert.Equal(
            TextPosition.At(1, 0),
            session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(2, 2),
            session.MoveBy(EditorSession.Caret(2, 2), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 0),
            session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Word, MoveDirection.Backward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(2, 2),
            session.MoveBy(EditorSession.Caret(2, 2), TextUnit.Word, MoveDirection.Forward, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void VerticalMotionPastEitherEndLandsOnTheDocumentEdge()
    {
        var session = EditorSession.Of("aaaa\nbbbb\ncccc");

        Assert.Equal(TextPosition.At(1, 0), session.MoveByLine(EditorSession.Caret(1, 3), -1, SelectionIntent.Move).Caret);
        Assert.Equal(TextPosition.At(3, 4), session.MoveByLine(EditorSession.Caret(3, 1), 1, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void APageIsAVerticalMoveOfManyLines()
    {
        var session = EditorSession.Of(string.Join("\n", Enumerable.Repeat("line", 40)));

        var down = session.MoveByLine(EditorSession.Caret(1, 2), 20, SelectionIntent.Move);
        var up = session.MoveByLine(down, -20, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(21, 2), down.Caret);
        Assert.Equal(TextPosition.At(1, 2), up.Caret);
    }

    [Fact]
    public void MotionToTheDocumentEdgesReachesBothEnds()
    {
        var session = EditorSession.Of("ab\ncd\n");

        Assert.Equal(
            TextPosition.At(1, 0),
            session.MoveToDocumentEdge(EditorSession.Caret(2, 1), DocumentEdge.Start, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(3, 0),
            session.MoveToDocumentEdge(EditorSession.Caret(1, 1), DocumentEdge.End, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void EditingAtTheVeryEndOfTheDocumentAppends()
    {
        var session = EditorSession.Of("ab");

        var after = session.Type(EditorSession.Caret(1, 2), "c");

        Assert.Equal("abc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 3), after.Caret);
    }

    [Fact]
    public void ASelectionOffTheEndOfTheDocumentIsBroughtBackInsideIt()
    {
        var session = EditorSession.Of("ab");

        var after = session.Type(EditorSession.Select(9, 9, 9, 40), "x");

        Assert.Equal("abx", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 3), after.Caret);
    }

    [Fact]
    public void AnEmptyDocumentTakesTypingWithoutMotionThrowing()
    {
        var session = EditorSession.Of(string.Empty);

        var caret = EditorSession.Caret(1, 0);
        caret = session.MoveByLine(caret, -1, SelectionIntent.Move);
        caret = session.MoveToLineEdge(caret, LineEdge.End, SelectionIntent.Move);
        caret = session.Type(caret, "x");

        Assert.Equal("x", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), caret.Caret);
    }

    [Fact]
    public void UndoRestoresTheSelectionTheEditWasMadeWith()
    {
        var session = EditorSession.Of("hello");

        var after = session.Type(EditorSession.Select(1, 1, 1, 4), "X");
        Assert.Equal("hXo", session.Document.Text);

        var restored = session.Undo();

        Assert.Equal("hello", session.Document.Text);
        Assert.True(restored.HasValue);
        Assert.Equal(TextPosition.At(1, 1), restored.GetValueOrDefault().Range.Start);
        Assert.Equal(TextPosition.At(1, 4), restored.GetValueOrDefault().Range.End);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);
    }

    [Fact]
    public void UndoWithNothingRecordedAnswersNothing()
    {
        var session = EditorSession.Of("abc");

        Assert.Null(session.Undo());
        Assert.Null(session.Redo());
    }

    [Fact]
    public void OptionsRefuseAnEmptyCommentToken()
    {
        Assert.Throws<ArgumentException>(() => EditorSession.Options(lineComment: string.Empty));
    }
}

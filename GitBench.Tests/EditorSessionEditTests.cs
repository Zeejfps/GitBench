using GitBench.Features.Editor;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>The edit vocabulary: what each keystroke turns into, and how much of it one undo is worth.</summary>
public sealed class EditorSessionEditTests
{
    [Fact]
    public void ARunOfTypingIsOneUndoStep()
    {
        var session = EditorSession.Of(string.Empty);

        var caret = EditorSession.Caret(1, 0);
        foreach (var rune in "abc") caret = session.Type(caret, rune.ToString());

        Assert.Equal("abc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 3), caret.Caret);
        Assert.Equal(1, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal(string.Empty, session.Document.Text);
    }

    [Fact]
    public void APasteStandsAloneAndDoesNotJoinTheTypingAroundIt()
    {
        var session = EditorSession.Of(string.Empty);

        var caret = session.Type(EditorSession.Caret(1, 0), "a");
        caret = session.Paste(caret, "one\ntwo");
        session.Type(caret, "b");

        Assert.Equal("aone\ntwob", session.Document.Text);
        Assert.Equal(3, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal("aone\ntwo", session.Document.Text);
        session.Undo();
        Assert.Equal("a", session.Document.Text);
    }

    [Fact]
    public void APasteThatIsOneCharacterLongIsStillItsOwnStep()
    {
        var session = EditorSession.Of(string.Empty);

        var caret = session.Type(EditorSession.Caret(1, 0), "a");
        session.Paste(caret, "b");

        Assert.Equal("ab", session.Document.Text);
        Assert.Equal(2, session.Journal.UndoDepth);
    }

    [Fact]
    public void TypingOverASelectionStartsANewStepAndThenContinuesIt()
    {
        var session = EditorSession.Of("hello");

        var caret = session.Type(EditorSession.Caret(1, 5), "!");
        caret = session.Type(EditorSession.Select(1, 0, 1, 5), "X");
        session.Type(caret, "y");

        Assert.Equal("Xy!", session.Document.Text);
        Assert.Equal(2, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal("hello!", session.Document.Text);
    }

    [Fact]
    public void TypingAfterAPasteDoesNotJoinThePastesStep()
    {
        var session = EditorSession.Of("");

        var caret = session.Paste(EditorSession.Caret(1, 0), "pasted");
        session.Type(caret, "x");

        Assert.Equal("pastedx", session.Document.Text);
        Assert.Equal(2, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal("pasted", session.Document.Text);
    }

    [Fact]
    public void BackspaceAtTheStartOfALineJoinsItToTheOneAbove()
    {
        var session = EditorSession.Of("ab\ncd");

        var after = session.Delete(EditorSession.Caret(2, 0), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("abcd", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);
    }

    [Fact]
    public void ForwardDeleteAtTheEndOfALineTakesTheLineBreak()
    {
        var session = EditorSession.Of("ab\ncd");

        var after = session.Delete(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Forward);

        Assert.Equal("abcd", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);
    }

    [Fact]
    public void ARunOfBackspacesIsOneUndoStep()
    {
        var session = EditorSession.Of("abcd");

        var caret = EditorSession.Caret(1, 4);
        for (var i = 0; i < 3; i++) caret = session.Delete(caret, TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("a", session.Document.Text);
        Assert.Equal(1, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal("abcd", session.Document.Text);
    }

    [Fact]
    public void DeleteWordFromMidWordTakesOnlyThatSide()
    {
        var backward = EditorSession.Of("hello world");
        var afterBackward = backward.Delete(EditorSession.Caret(1, 3), TextUnit.Word, MoveDirection.Backward);

        Assert.Equal("lo world", backward.Document.Text);
        Assert.Equal(TextPosition.At(1, 0), afterBackward.Caret);

        var forward = EditorSession.Of("hello world");
        var afterForward = forward.Delete(EditorSession.Caret(1, 3), TextUnit.Word, MoveDirection.Forward);

        Assert.Equal("helworld", forward.Document.Text);
        Assert.Equal(TextPosition.At(1, 3), afterForward.Caret);
    }

    [Fact]
    public void EachWordDeleteIsItsOwnUndoStep()
    {
        var session = EditorSession.Of("one two three");

        var caret = EditorSession.Caret(1, 13);
        caret = session.Delete(caret, TextUnit.Word, MoveDirection.Backward);
        session.Delete(caret, TextUnit.Word, MoveDirection.Backward);

        Assert.Equal("one ", session.Document.Text);
        Assert.Equal(2, session.Journal.UndoDepth);
    }

    [Fact]
    public void NewlineCopiesTheIndentationOfTheLineItBreaks()
    {
        var session = EditorSession.Of("    foo\nbar");

        var after = session.InsertNewline(EditorSession.Caret(1, 7));

        Assert.Equal("    foo\n    \nbar", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 4), after.Caret);
    }

    [Fact]
    public void NewlineCopiesTheIndentationOfALineThatIsOnlyWhitespace()
    {
        var session = EditorSession.Of("      ");

        var after = session.InsertNewline(EditorSession.Caret(1, 6));

        Assert.Equal("      \n      ", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 6), after.Caret);
    }

    [Fact]
    public void NewlineAtColumnZeroCopiesNoIndentation()
    {
        var session = EditorSession.Of("    foo");

        var after = session.InsertNewline(EditorSession.Caret(1, 0));

        Assert.Equal("\n    foo", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 0), after.Caret);
    }

    [Fact]
    public void NewlineReplacesTheSelectionAndIndentsFromItsStart()
    {
        var session = EditorSession.Of("    foo bar");

        var after = session.InsertNewline(EditorSession.Select(1, 7, 1, 11));

        Assert.Equal("    foo\n    ", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 4), after.Caret);
    }

    [Fact]
    public void IndentAtACaretRunsToTheNextTabStop()
    {
        var session = EditorSession.Of("ab");

        var after = session.Indent(EditorSession.Caret(1, 2));

        Assert.Equal("ab  ", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 4), after.Caret);
    }

    [Fact]
    public void IndentOverAMultiLineSelectionMovesWholeLinesAndKeepsCoveringThem()
    {
        var session = EditorSession.Of("a\nb\nc");

        var after = session.Indent(EditorSession.Select(1, 0, 3, 1));

        Assert.Equal("    a\n    b\n    c", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 0), after.Anchor);
        Assert.Equal(TextPosition.At(3, 5), after.Caret);
        Assert.Equal(1, session.Journal.UndoDepth);
    }

    [Fact]
    public void IndentAndOutdentOverABlockAreEachOther()
    {
        var session = EditorSession.Of("a\nb\nc");

        var selection = EditorSession.Select(1, 0, 3, 1);
        selection = session.Indent(selection);
        selection = session.Outdent(selection);

        Assert.Equal("a\nb\nc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 0), selection.Anchor);
        Assert.Equal(TextPosition.At(3, 1), selection.Caret);
    }

    [Fact]
    public void IndentLeavesBlankLinesInsideTheBlockAlone()
    {
        var session = EditorSession.Of("a\n\nc");

        session.Indent(EditorSession.Select(1, 0, 3, 1));

        Assert.Equal("    a\n\n    c", session.Document.Text);
    }

    [Fact]
    public void ASelectionEndingAtTheStartOfALineDoesNotIndentThatLine()
    {
        var session = EditorSession.Of("a\nb\nc");

        session.Indent(EditorSession.Select(1, 0, 3, 0));

        Assert.Equal("    a\n    b\nc", session.Document.Text);
    }

    [Fact]
    public void ATwoLineSelectionIsIndentedRatherThanReplacedByTheIndent()
    {
        var session = EditorSession.Of("a\nb\nc");

        session.Indent(EditorSession.Select(1, 0, 2, 0));

        Assert.Equal("    a\nb\nc", session.Document.Text);
    }

    [Fact]
    public void ASelectionEndingAtTheStartOfTheNextLineKeepsTheLineItStartedOn()
    {
        var session = EditorSession.Of("abc\nb\nc");

        session.Indent(EditorSession.Select(1, 2, 2, 0));

        Assert.Equal("    abc\nb\nc", session.Document.Text);
    }

    [Fact]
    public void OutdentWithNoLeadingWhitespaceRecordsNothing()
    {
        var session = EditorSession.Of("a\nb");

        var selection = EditorSession.Select(1, 0, 2, 1);
        var after = session.Outdent(selection);

        Assert.Equal("a\nb", session.Document.Text);
        Assert.Equal(0, session.Document.Revision);
        Assert.Equal(0, session.Journal.UndoDepth);
        Assert.Equal(selection, after);
    }

    [Fact]
    public void OutdentSkipsTheLinesWithNothingToRemove()
    {
        var session = EditorSession.Of("    a\nb\n        c");

        session.Outdent(EditorSession.Select(1, 0, 3, 1));

        Assert.Equal("a\nb\n    c", session.Document.Text);
        Assert.Equal(1, session.Journal.UndoDepth);
    }

    [Fact]
    public void OutdentTakesLessThanAFullIndentWhenThatIsAllThereIs()
    {
        var session = EditorSession.Of("  a");

        session.Outdent(EditorSession.Caret(1, 3));

        Assert.Equal("a", session.Document.Text);
    }

    [Fact]
    public void OutdentAtACaretStillMovesTheWholeLine()
    {
        var session = EditorSession.Of("    abc");

        var after = session.Outdent(EditorSession.Caret(1, 6));

        Assert.Equal("abc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);
    }

    [Fact]
    public void ASelectionAnEditConsumesEntirelyComesBackAsACaret()
    {
        var session = EditorSession.Of("        ab");

        var after = session.Outdent(EditorSession.Select(1, 1, 1, 3));

        Assert.Equal("    ab", session.Document.Text);
        Assert.True(after.IsEmpty);
        Assert.Equal(TextPosition.At(1, 0), after.Caret);
    }

    [Fact]
    public void ToggleCommentOnAMixedSelectionCommentsTheLinesThatAreNot()
    {
        var session = EditorSession.Of("a\n// b\nc", EditorSession.Options(lineComment: "//"));

        session.ToggleLineComment(EditorSession.Select(1, 0, 3, 1));

        Assert.Equal("// a\n// b\n// c", session.Document.Text);
        Assert.Equal(1, session.Journal.UndoDepth);
    }

    [Fact]
    public void ToggleCommentOnAFullyCommentedSelectionUncommentsIt()
    {
        var session = EditorSession.Of("// a\n//b\n    // c", EditorSession.Options(lineComment: "//"));

        session.ToggleLineComment(EditorSession.Select(1, 0, 3, 1));

        Assert.Equal("a\nb\n    c", session.Document.Text);
    }

    [Fact]
    public void ToggleCommentInsertsAtEachLinesOwnIndentation()
    {
        var session = EditorSession.Of("a\n    b", EditorSession.Options(lineComment: "//"));

        session.ToggleLineComment(EditorSession.Select(1, 0, 2, 5));

        Assert.Equal("// a\n    // b", session.Document.Text);
    }

    [Fact]
    public void ToggleCommentNeitherReadsNorTouchesBlankLines()
    {
        var session = EditorSession.Of("// a\n\n// c", EditorSession.Options(lineComment: "//"));

        session.ToggleLineComment(EditorSession.Select(1, 0, 3, 4));

        Assert.Equal("a\n\nc", session.Document.Text);
    }

    [Fact]
    public void ToggleCommentOnASelectionOfNothingButBlankLinesRecordsNothing()
    {
        var session = EditorSession.Of("\n\n", EditorSession.Options(lineComment: "//"));

        session.ToggleLineComment(EditorSession.Select(1, 0, 3, 0));

        Assert.Equal("\n\n", session.Document.Text);
        Assert.Equal(0, session.Journal.UndoDepth);
    }

    [Fact]
    public void ToggleCommentDeclinesForALanguageWithNoLineComment()
    {
        var session = EditorSession.Of("a\nb");

        var selection = EditorSession.Select(1, 0, 2, 1);
        var after = session.ToggleLineComment(selection);

        Assert.Equal("a\nb", session.Document.Text);
        Assert.Equal(0, session.Journal.UndoDepth);
        Assert.Equal(selection, after);
    }

    [Fact]
    public void IndentingWithTabsPutsATabInTheFile()
    {
        var session = EditorSession.Of("a\nb", EditorSession.Options(indent: IndentStyle.Tabs));

        session.Indent(EditorSession.Select(1, 0, 2, 1));

        Assert.Equal("\ta\n\tb", session.Document.Text);
    }

    [Fact]
    public void EveryEditIsOneTransactionHoweverManyLinesItTouches()
    {
        var session = EditorSession.Of("a\nb\nc\nd");

        session.Indent(EditorSession.Select(1, 0, 4, 1));
        Assert.Equal(1, session.Journal.UndoDepth);

        session.Undo();
        Assert.Equal("a\nb\nc\nd", session.Document.Text);

        session.Redo();
        Assert.Equal("    a\n    b\n    c\n    d", session.Document.Text);
    }

    [Fact]
    public void PastedTextLosesTheControlCharactersAFileCannotShow()
    {
        var session = EditorSession.Of("x");

        var after = session.Paste(EditorSession.Caret(1, 0), "a\0b\u001bc\td");

        Assert.Equal("abc\tdx", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 5), after.Caret);
    }

    [Fact]
    public void APasteOfNothingButControlCharactersRecordsNoStep()
    {
        var session = EditorSession.Of("x");

        session.Paste(EditorSession.Caret(1, 0), "\0");

        Assert.Equal("x", session.Document.Text);
        Assert.Equal(0, session.Journal.UndoDepth);
    }

    [Fact]
    public void ADocumentsSettingsAreReadOffTheFileItself()
    {
        var tabbed = EditOptions.For("Program.cs", LineEnding.CrLf, ["class C", "\tvoid M()", "\t{"]);
        Assert.Equal(IndentStyle.Tabs, tabbed.Indent);
        Assert.Equal("//", tabbed.LineComment);
        Assert.Equal("\r\n", tabbed.EolText);

        var spaced = EditOptions.For("run.py", LineEnding.Lf, ["def f():", "    return 1"]);
        Assert.Equal(IndentStyle.Spaces, spaced.Indent);
        Assert.Equal("#", spaced.LineComment);

        Assert.Null(EditOptions.For("page.html", LineEnding.Lf, []).LineComment);
        Assert.Null(EditOptions.For("notes.unknownext", LineEnding.Lf, []).LineComment);
    }
}

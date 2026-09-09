using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>Undo, and what a reader expects one undo step to be worth.</summary>
public sealed class EditorEditJournalTests
{
    private static TextRange Caret(int line, int column) => TextRange.Caret(TextPosition.At(line, column));

    private static TextPosition Enter(EditJournal journal, EditKind kind, TextPosition caret, string text) =>
        journal.Apply(new EditTransaction(
            kind,
            new[] { new TextEdit(TextRange.Caret(caret), text) },
            SelectionRange.At(caret),
            AnchorBias.After)).Caret;

    private static TextPosition Backspace(EditJournal journal, TextPosition caret, int count)
    {
        var start = TextPosition.At(caret.Line.Value, caret.Column.Value - count);
        return journal.Apply(new EditTransaction(
            EditKind.Delete,
            new[] { new TextEdit(new TextRange(start, caret), string.Empty) },
            SelectionRange.At(caret),
            AnchorBias.After)).Caret;
    }

    [Fact]
    public void ARunOfTypingIsOneUndoStep()
    {
        var document = TextDocument.FromText("()");
        var journal = new EditJournal(document);

        var caret = TextPosition.At(1, 1);
        foreach (var c in "hello")
            caret = Enter(journal, EditKind.Insert, caret, c.ToString());

        Assert.Equal("(hello)", document.Text);
        Assert.Equal(1, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("()", document.Text);
        Assert.False(journal.CanUndo);
    }

    [Fact]
    public void TypingSomewhereElseStartsANewStep()
    {
        var document = TextDocument.FromText("ab");
        var journal = new EditJournal(document);

        Enter(journal, EditKind.Insert, TextPosition.At(1, 1), "x");
        Enter(journal, EditKind.Insert, TextPosition.At(1, 0), "y");

        Assert.Equal("yaxb", document.Text);
        Assert.Equal(2, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("axb", document.Text);
    }

    [Fact]
    public void APasteIsItsOwnStepAndDoesNotSwallowTheTypingAroundIt()
    {
        var document = TextDocument.FromText(string.Empty);
        var journal = new EditJournal(document);

        var caret = Enter(journal, EditKind.Insert, TextPosition.At(1, 0), "a");
        caret = Enter(journal, EditKind.Boundary, caret, "PASTED");
        Enter(journal, EditKind.Insert, caret, "b");

        Assert.Equal("aPASTEDb", document.Text);
        Assert.Equal(3, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("aPASTED", document.Text);
        journal.Undo();
        Assert.Equal("a", document.Text);
    }

    [Fact]
    public void AWordDeleteIsItsOwnStepAndDoesNotMergeWithTheBackspacesAroundIt()
    {
        var document = TextDocument.FromText("one two three");
        var journal = new EditJournal(document);

        var caret = Backspace(journal, TextPosition.At(1, 13), 1);
        caret = Backspace(journal, caret, 1);
        Assert.Equal(1, journal.UndoDepth);

        journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[] { new TextEdit(new TextRange(TextPosition.At(1, 8), caret), string.Empty) },
            SelectionRange.At(caret),
            AnchorBias.After));

        Assert.Equal("one two ", document.Text);
        Assert.Equal(2, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("one two thr", document.Text);
        journal.Undo();
        Assert.Equal("one two three", document.Text);
    }

    [Fact]
    public void AnEditThatReplacesASelectionNeverMergesIntoTheTypingBeforeIt()
    {
        var document = TextDocument.FromText("abcdef");
        var journal = new EditJournal(document);

        var caret = Enter(journal, EditKind.Insert, TextPosition.At(1, 0), "x");
        var selection = new SelectionRange(caret, TextPosition.At(1, 4));
        journal.Apply(new EditTransaction(
            EditKind.Insert,
            new[] { new TextEdit(selection.Range, "y") },
            selection,
            AnchorBias.After));

        Assert.Equal("xydef", document.Text);
        Assert.Equal(2, journal.UndoDepth);
    }

    [Fact]
    public void UndoRestoresTheSelectionTheEditWasMadeWithAndRedoTheOneItLeft()
    {
        var document = TextDocument.FromText("abcdef");
        var journal = new EditJournal(document);
        var selection = new SelectionRange(TextPosition.At(1, 1), TextPosition.At(1, 4));

        var after = journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[] { new TextEdit(selection.Range, "Z") },
            selection,
            AnchorBias.After));

        Assert.Equal("aZef", document.Text);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);

        Assert.Equal(selection.Range, journal.Undo());
        Assert.Equal("abcdef", document.Text);

        Assert.Equal(Caret(1, 2), journal.Redo());
        Assert.Equal("aZef", document.Text);
    }

    [Fact]
    public void AnEditAfterAnUndoClearsTheRedoPath()
    {
        var document = TextDocument.FromText(string.Empty);
        var journal = new EditJournal(document);

        Enter(journal, EditKind.Boundary, TextPosition.At(1, 0), "first");
        journal.Undo();
        Assert.True(journal.CanRedo);

        Enter(journal, EditKind.Boundary, TextPosition.At(1, 0), "second");

        Assert.False(journal.CanRedo);
        Assert.Null(journal.Redo());
        Assert.Equal("second", document.Text);
    }

    [Fact]
    public void TypingAfterAnUndoDoesNotMergeIntoTheStepThatWasJustPopped()
    {
        var document = TextDocument.FromText(string.Empty);
        var journal = new EditJournal(document);

        var caret = Enter(journal, EditKind.Insert, TextPosition.At(1, 0), "a");
        Enter(journal, EditKind.Insert, caret, "b");
        Assert.Equal(1, journal.UndoDepth);

        journal.Undo();
        Assert.Equal(string.Empty, document.Text);

        Enter(journal, EditKind.Insert, TextPosition.At(1, 0), "c");

        Assert.Equal(1, journal.UndoDepth);
        journal.Undo();
        Assert.Equal(string.Empty, document.Text);
    }

    [Fact]
    public void UndoAndRedoWalkTheWholeHistoryBothWays()
    {
        var document = TextDocument.FromText("start\n");
        var journal = new EditJournal(document);

        Enter(journal, EditKind.Boundary, TextPosition.At(2, 0), "one");
        Enter(journal, EditKind.Boundary, TextPosition.At(2, 3), " two");
        journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[] { new TextEdit(new TextRange(TextPosition.At(1, 0), TextPosition.At(1, 5)), "END") },
            SelectionRange.At(TextPosition.At(1, 5)),
            AnchorBias.After));

        Assert.Equal("END\none two", document.Text);

        journal.Undo();
        journal.Undo();
        journal.Undo();
        Assert.Equal("start\n", document.Text);
        Assert.False(journal.CanUndo);

        journal.Redo();
        journal.Redo();
        journal.Redo();
        Assert.Equal("END\none two", document.Text);
        Assert.False(journal.CanRedo);
    }

    [Fact]
    public void EveryEditInOneTransactionUndoesTogether()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");
        var journal = new EditJournal(document);

        journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[]
            {
                new TextEdit(TextRange.Caret(TextPosition.At(3, 0)), "// "),
                new TextEdit(TextRange.Caret(TextPosition.At(1, 0)), "// "),
            },
            SelectionRange.At(TextPosition.At(1, 0)),
            AnchorBias.Before));

        Assert.Equal("// one\ntwo\n// three", document.Text);
        Assert.Equal(1, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("one\ntwo\nthree", document.Text);
    }

    [Fact]
    public void ATransactionThatChangesNoTextCostsNeitherAStepNorTheRedoPath()
    {
        var document = TextDocument.FromText("abc");
        var journal = new EditJournal(document);

        Enter(journal, EditKind.Boundary, TextPosition.At(1, 0), "x");
        journal.Undo();

        journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[] { new TextEdit(TextRange.Caret(TextPosition.At(1, 1)), string.Empty) },
            SelectionRange.At(TextPosition.At(1, 1)),
            AnchorBias.After));

        Assert.Equal(0, journal.UndoDepth);
        Assert.True(journal.CanRedo);
        Assert.Equal("abc", document.Text);
    }

    [Fact]
    public void ALongRunOfTypingUndoesToExactlyWhereItStartedAndRedoesBack()
    {
        var document = TextDocument.FromText("(x)");
        var journal = new EditJournal(document);
        var before = Caret(1, 1);

        var caret = before.Start;
        foreach (var c in "abcdefgh")
            caret = Enter(journal, EditKind.Insert, caret, c.ToString());

        Assert.Equal("(abcdefghx)", document.Text);
        Assert.Equal(1, journal.UndoDepth);

        Assert.Equal(before, journal.Undo());
        Assert.Equal("(x)", document.Text);

        Assert.Equal(Caret(1, 9), journal.Redo());
        Assert.Equal("(abcdefghx)", document.Text);
    }

    [Fact]
    public void ARunOfBackspacesUndoesToExactlyWhereItStartedAndRedoesBack()
    {
        var document = TextDocument.FromText("one two three");
        var journal = new EditJournal(document);
        var before = Caret(1, 13);

        var caret = before.Start;
        for (var i = 0; i < 5; i++)
            caret = Backspace(journal, caret, 1);

        Assert.Equal("one two ", document.Text);
        Assert.Equal(1, journal.UndoDepth);

        Assert.Equal(before, journal.Undo());
        Assert.Equal("one two three", document.Text);

        Assert.Equal(Caret(1, 8), journal.Redo());
        Assert.Equal("one two ", document.Text);
    }

    [Fact]
    public void ARunBrokenByABoundaryUndoesAsThreeStepsInTheOrderItWasTyped()
    {
        var document = TextDocument.FromText(string.Empty);
        var journal = new EditJournal(document);

        var caret = TextPosition.At(1, 0);
        foreach (var c in "abc") caret = Enter(journal, EditKind.Insert, caret, c.ToString());
        caret = Enter(journal, EditKind.Boundary, caret, "PASTED");
        foreach (var c in "def") caret = Enter(journal, EditKind.Insert, caret, c.ToString());

        Assert.Equal("abcPASTEDdef", document.Text);
        Assert.Equal(3, journal.UndoDepth);

        journal.Undo();
        Assert.Equal("abcPASTED", document.Text);
        journal.Undo();
        Assert.Equal("abc", document.Text);
        journal.Undo();
        Assert.Equal(string.Empty, document.Text);

        journal.Redo();
        journal.Redo();
        journal.Redo();
        Assert.Equal("abcPASTEDdef", document.Text);
    }

    [Fact]
    public void TheRowProjectionFollowsAWholeRunThroughUndoAndRedo()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");
        EditorRowSet rows = null!;
        var journal = new EditJournal(document, edit => rows.Reproject(edit));
        rows = new EditorRowSet(document, new LocalizationService(new State<Locale>(Locale.En)));

        var caret = TextPosition.At(2, 3);
        foreach (var c in "xyz") caret = Enter(journal, EditKind.Insert, caret, c.ToString());
        journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[]
            {
                new TextEdit(TextRange.Caret(TextPosition.At(3, 0)), "// "),
                new TextEdit(TextRange.Caret(TextPosition.At(1, 0)), "// "),
            },
            SelectionRange.At(caret),
            AnchorBias.Before));
        AssertRowsMatch(document, rows);

        journal.Undo();
        AssertRowsMatch(document, rows);
        journal.Undo();
        Assert.Equal("one\ntwo\nthree", document.Text);
        AssertRowsMatch(document, rows);

        journal.Redo();
        AssertRowsMatch(document, rows);
        journal.Redo();
        Assert.Equal("// one\ntwoxyz\n// three", document.Text);
        AssertRowsMatch(document, rows);
    }

    private static void AssertRowsMatch(TextDocument document, EditorRowSet rows)
    {
        Assert.Equal(document.LineCount, rows.Rows.Count);
        for (var i = 0; i < document.LineCount; i++)
            Assert.Equal(
                document.Line(new FileLine(i + 1)),
                Assert.IsType<DiffRow.Line>(rows.Rows[i]).Text.Raw);
    }

    [Fact]
    public void TheHistoryStopsAtItsCapAndKeepsTheNewestSteps()
    {
        var document = TextDocument.FromText(string.Empty);
        var journal = new EditJournal(document);

        for (var i = 0; i < EditJournal.MaxDepth + 44; i++)
            Enter(journal, EditKind.Boundary, TextPosition.At(1, 0), "x");

        Assert.Equal(EditJournal.MaxDepth, journal.UndoDepth);

        for (var i = 0; i < EditJournal.MaxDepth; i++)
            journal.Undo();

        Assert.False(journal.CanUndo);
        Assert.Equal(new string('x', 44), document.Text);
    }

    [Fact]
    public void TheBiasDecidesWhetherASelectionAtTheEditStartTravelsWithTheInsertion()
    {
        static TextPosition Carry(AnchorBias bias)
        {
            var document = TextDocument.FromText("abcd");
            return new EditJournal(document).Apply(new EditTransaction(
                EditKind.Boundary,
                new[] { new TextEdit(Caret(1, 2), "XY") },
                SelectionRange.At(TextPosition.At(1, 2)),
                bias)).Caret;
        }

        Assert.Equal(TextPosition.At(1, 2), Carry(AnchorBias.Before));
        Assert.Equal(TextPosition.At(1, 4), Carry(AnchorBias.After));
    }

    [Fact]
    public void TheCarriedSelectionMovesByEveryEditInTheTransaction()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");
        var journal = new EditJournal(document);

        var after = journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[]
            {
                new TextEdit(Caret(1, 0), "x\n"),
                new TextEdit(Caret(2, 0), "y\n"),
            },
            SelectionRange.At(TextPosition.At(3, 0)),
            AnchorBias.Before));

        Assert.Equal("x\ny\none\ntwo\nthree", document.Text);
        Assert.Equal(TextPosition.At(5, 0), after.Caret);
        Assert.Equal("three", document.Line(after.Caret.Line));
    }

    [Fact]
    public void TheCarriedSelectionStillPointsAtItsOwnTextAfterARunOfEditsAboveAndBesideIt()
    {
        var document = TextDocument.FromText("alpha\nbeta\ngamma");
        var journal = new EditJournal(document);

        var carried = SelectionRange.At(TextPosition.At(3, 0));
        for (var i = 0; i < 40; i++)
        {
            carried = journal.Apply(new EditTransaction(
                EditKind.Boundary,
                new[] { new TextEdit(Caret(1, 0), "x"), new TextEdit(Caret(2, 0), "y\n") },
                carried,
                AnchorBias.Before));
        }

        Assert.Equal("gamma", document.Line(carried.Caret.Line));
        Assert.Equal(0, carried.Caret.Column.Value);
    }

    [Fact]
    public void TheCarriedSelectionMovesByTheEditTheDocumentWidenedToKeepACrlfWhole()
    {
        var document = TextDocument.FromText("a\nbb\ncc");
        var journal = new EditJournal(document);

        var after = journal.Apply(new EditTransaction(
            EditKind.Boundary,
            new[] { new TextEdit(Caret(1, 1), "\r") },
            SelectionRange.At(TextPosition.At(2, 0)),
            AnchorBias.After));

        Assert.Equal("a\r\nbb\ncc", document.Text);
        Assert.Equal(3, document.LineCount);
        Assert.Equal(TextPosition.At(2, 0), after.Caret);
        Assert.Equal("bb", document.Line(after.Caret.Line));
    }
}

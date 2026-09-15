
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Theming;

using Xunit;

using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The plumbing between a document being typed into and the parse that describes it: which
/// revision a result is stamped with, which results are refused, and what a document's lifetime
/// does to the tree held for it.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class DocumentAnnotationsTests(CodeIntelFixture fixture)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string Source = """
        using System;

        internal sealed class Widget
        {
            public int Count() => 1;
        }
        """;

    [Fact]
    public void TypingAtTheHeadOfALineRepaintsTheRowsBeneathTheCaret()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);

        var buffer = Open(documents, "Widget.cs", Source);
        Quiet(annotations, posted);

        var keyword = Assert.Single(SpansOf(buffer, row: 0), span => span.Start == 0);

        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "var q=1; ");
        Quiet(annotations, posted);

        var after = SpansOf(buffer, row: 0);
        Assert.Contains(after, span => span.Start == 9 && span.Length == 5 && span.Slot == keyword.Slot);
        Assert.DoesNotContain(after, span => span.Start == 0 && span.Length == 5 && span.Slot == keyword.Slot);
    }

    [Fact]
    public void ADeclarationTypedInGetsFoldChevronsOfItsOwn()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);

        var buffer = Open(documents, "Widget.cs", Source);
        buffer.Rows.SetFolds(FoldState.Open("Widget.cs"));
        Quiet(annotations, posted);

        // Only the class is foldable to start with: the one method is a single line.
        Assert.Equal([3], FoldableRows(buffer));

        buffer.Session.Type(
            SelectionRange.At(TextPosition.At(5, "    public int Count() => 1;".Length)),
            "\n\n    public string Added()\n    {\n        return \"x\";\n    }");
        Quiet(annotations, posted);

        // The chevron is a fold mark on the row the declaration starts at, and only an outline of
        // the buffer can put one there: the one from the read named a file four lines shorter and
        // knew nothing about this method.
        Assert.Equal([3, 7], FoldableRows(buffer));
    }

    [Fact]
    public void AnUndoOfAWholeTransactionIsStampedWithTheLastOfItsRevisions()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);

        var buffer = Open(documents, "Widget.cs", Source);
        Quiet(annotations, posted);

        // Three lines pasted in as one step: undoing it applies the reversal back to front, one
        // edit and one revision each, and the stamp has to be the last of them or every result is
        // refused.
        buffer.Session.Paste(
            SelectionRange.At(TextPosition.At(1, 0)), "// one\n// two\n// three\n");
        Quiet(annotations, posted);

        var undone = buffer.Session.Journal.Undo();
        Assert.NotNull(undone);
        Quiet(annotations, posted);

        Assert.Equal(Source, buffer.Session.Document.Text);
        var comments = buffer.Rows.Rows.OfType<DiffRow.Line>()
            .SelectMany(row => row.Spans ?? [])
            .Count(span => span.Slot == TokenColorSlot.Comment);
        Assert.Equal(0, comments);
    }

    [Fact]
    public void AParseOfARevisionTheDocumentHasMovedPastIsRefused()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);

        var buffer = Open(documents, "Widget.cs", Source);
        Quiet(annotations, posted);

        var before = Describe(buffer);

        // The parse is held on the dispatcher while the reader types again, which is the race the
        // revision stamp exists for: what comes back describes a file that no longer exists.
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// ");
        Assert.True(annotations.Settled().Wait(Wait), "The parse worker never went quiet.");
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 3)), "x");
        posted.Drain();

        Assert.Equal(before, Describe(buffer));
    }

    [Fact]
    public void ReplacingTheFileOnDiskBuildsAFreshTreeRatherThanEditingTheOldOne()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);
        var repo = documents.For(Guid.NewGuid());

        var first = Open(repo, "Widget.cs", Source);
        Quiet(annotations, posted);

        // A reload replaces the document wholesale and produces no edits at all, so the buffer is
        // forgotten and a new one opened over the new text. Editing the old tree with whatever
        // arrives next is the wrong default here.
        const string reloaded = "internal sealed class Other\n{\n    public int Answer() => 42;\n}\n";
        var second = Open(repo, "Widget.cs", reloaded);
        Assert.NotSame(first, second);
        Quiet(annotations, posted);

        second.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// ");
        Quiet(annotations, posted);

        Assert.Contains(
            SpansOf(second, row: 0),
            span => span.Start == 0 && span.Slot == TokenColorSlot.Comment);
    }

    [Fact]
    public void AFileTreeSitterDoesNotColourIsLeftAlone()
    {
        var documents = new TestDocuments.Empty();
        var posted = new QueuedDispatcher();
        using var annotations = Producer(documents, posted);

        // TextMate colours this one, and it does so once, at open. Publishing an annotation with
        // no highlight in it would blank that out.
        var buffer = Open(documents, "notes.txt", "one\ntwo\n");
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");
        Quiet(annotations, posted);

        Assert.Empty(SpansOf(buffer, row: 0));
    }

    // ---- The stamp on a parse of the file, not of the buffer ---------------------------------

    [Fact]
    public void AParseOfTheFileOnDiskIsRefusedOnceSomethingHasBeenTypedOverIt()
    {
        var documents = new TestDocuments.Empty();
        var buffer = Open(documents, "Widget.cs", Source);

        Assert.True(buffer.ApplyRead(new DiffAnnotations(null, fixture.Outline(Source), null)));

        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// ");

        Assert.False(buffer.ApplyRead(new DiffAnnotations(null, fixture.Outline(Source), null)));
    }

    [Fact]
    public void AReReadThatFindsTheDocumentUnchangedDescribesTheRevisionItIsAt()
    {
        var documents = new TestDocuments.Empty();
        var repo = documents.For(Guid.NewGuid());
        var buffer = Open(repo, "Widget.cs", Source);

        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// ");
        var edited = buffer.Session.Document.Text;

        // A parse of the file as it was opened describes it no longer, and is refused.
        Assert.False(buffer.ApplyRead(new DiffAnnotations(null, fixture.Outline(edited), null)));

        // The save lands and the echo that follows it re-reads a file that now says what the
        // document does. Stamping that read with the revision the buffer was opened at refuses a
        // parse which is in fact current, which is why the colouring did not recover on save.
        repo.MarkSaved("Widget.cs");
        var reopened = repo.Open(
            "Widget.cs",
            FilePreviewFixture.Of(edited.Split('\n'), endsWithNewline: false),
            FilePreviewFixture.Reversible,
            null);

        Assert.Same(buffer, reopened);
        Assert.False(buffer.Opened.Describes(buffer.Session.Document));
        Assert.True(buffer.ApplyRead(new DiffAnnotations(null, fixture.Outline(edited), null)));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private DocumentAnnotations Producer(IDocumentStore documents, IUiDispatcher dispatcher)
    {
        var annotations = new DocumentAnnotations(
            documents, dispatcher, fixture.Grammars, fixture.Highlighter, fixture.Symbols, TimeSpan.Zero);
        annotations.Start();
        return annotations;
    }

    // Blocking rather than awaited, and deliberately: a buffer belongs to the thread that built it,
    // so the work the worker hands back has to be run on this one — an await would resume the test
    // on a pool thread and the projection would refuse it.
    private static void Quiet(DocumentAnnotations annotations, QueuedDispatcher posted)
    {
        // More than one round because a parse the worker could not follow asks the UI thread for
        // the text again, which puts another message in the queue.
        for (var round = 0; round < 3; round++)
        {
            Assert.True(annotations.Settled().Wait(Wait), "The parse worker never went quiet.");
            posted.Drain();
        }
    }

    private static EditorBuffer Open(IDocumentStore documents, string path, string text) =>
        Open(documents.For(Guid.NewGuid()), path, text);

    private static EditorBuffer Open(IRepoDocuments documents, string path, string text) =>
        documents.Open(
            path,
            FilePreviewFixture.Of(text.Split('\n'), endsWithNewline: false),
            FilePreviewFixture.Reversible,
            null)
        ?? throw new InvalidOperationException($"{path} did not open for editing.");

    /// <summary>The 1-based rows carrying a fold chevron.</summary>
    private static int[] FoldableRows(EditorBuffer buffer) =>
        [.. buffer.Rows.Rows
            .Select((row, index) => (row, index))
            .Where(entry => entry.row is DiffRow.Line { Fold: not null })
            .Select(entry => entry.index + 1)];

    private static IReadOnlyList<TokenSpan> SpansOf(EditorBuffer buffer, int row) =>
        buffer.Rows.Rows[row] is DiffRow.Line line ? line.Spans ?? [] : [];

    private static string Describe(EditorBuffer buffer) =>
        string.Join(
            " | ",
            buffer.Rows.Rows.OfType<DiffRow.Line>().Select(row =>
                string.Join(' ', (row.Spans ?? []).Select(s => $"{s.Start}+{s.Length}:{s.Slot}"))));
}

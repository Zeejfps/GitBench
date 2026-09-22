using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>Completion without a language server: how a prefix matches, what the file offers, and
/// how the list opens, narrows, steers and closes as the caret moves.</summary>
public sealed class EditorCompletionTests
{
    private static CompletionItem Word(string label) => new(label, CompletionKind.AWord);

    private static IReadOnlyList<string> Labels(IEnumerable<RankedCompletion> ranked) =>
        ranked.Select(r => r.Item.Label).ToList();

    [Fact]
    public void CamelHumpsMatchTheStartOfEachHump()
    {
        var match = CompletionMatcher.Match("gBN", "getBranchName");

        Assert.NotNull(match);
        Assert.Equal([0, 3, 9], match!.Positions);
        Assert.Null(CompletionMatcher.Match("gxN", "getBranchName"));
    }

    [Fact]
    public void APrefixOutranksHumpsWhichOutrankASubstring()
    {
        var ranked = CompletionMatcher.Rank("br", [Word("getBranch"), Word("abrupt"), Word("branch")]);

        Assert.Equal(["branch", "getBranch", "abrupt"], Labels(ranked));
    }

    [Fact]
    public void AnExactCasePrefixOutranksAnIgnoredCaseOne()
    {
        var ranked = CompletionMatcher.Rank("Foo", [Word("fooBar"), Word("FooBar")]);

        Assert.Equal(["FooBar", "fooBar"], Labels(ranked));
    }

    [Fact]
    public void TheFileOffersItsWordsButNotTheOneBeingTyped()
    {
        var document = TextDocument.FromText("int counter = 0;\ncou");

        var items = LocalCompletions.Collect(document, TextPosition.At(2, 3), null, []);
        var labels = items.Select(i => i.Label).ToHashSet();

        Assert.Contains("counter", labels);
        Assert.Contains("int", labels);
        Assert.DoesNotContain("cou", labels);
    }

    [Fact]
    public void KeywordsComeFromTheHighlightQuerysLiterals()
    {
        var keywords = CompletionKeywords.FromQuery(
            "[\"if\" \"else\" \"return\"] @keyword\n((identifier) @x (#match? @x \"^[A-Z]+$\"))\n\"(\" @punctuation");

        Assert.Equal(new HashSet<string> { "if", "else", "return" }, keywords.ToHashSet());
    }

    [Fact]
    public void CSharpHasItsKeywordsFromTheBundledQuery()
    {
        var keywords = CompletionKeywords.For("Program.cs");

        Assert.Contains("class", keywords);
        Assert.Contains("return", keywords);
    }

    [Fact]
    public void OutlineDeclarationsCarryTheirKind()
    {
        var document = TextDocument.FromText("class Widget {}\nWi");
        var outline = new FileOutline([
            new OutlineNode("Widget", SymbolKind.Class, null, 1, 1, 1, new FileLine(1), new RawColumn(6), []),
        ]);

        var items = LocalCompletions.Collect(document, TextPosition.At(2, 2), outline, []);

        Assert.Equal(new CompletionKind.Symbol(SymbolKind.Class), items.Single(i => i.Label == "Widget").Kind);
    }

    [Fact]
    public void TypingTheFirstLetterOfAWordOpensTheList()
    {
        var document = TextDocument.FromText("counter\nc");
        var session = new CompletionSession();

        session.Typed(document, TextPosition.At(2, 1), () => [Word("counter")]);

        Assert.True(session.IsOpen);
        Assert.Equal("c", session.Current!.Prefix);
    }

    [Fact]
    public void TypingInTheMiddleOfAWordDoesNotOpenTheList()
    {
        var document = TextDocument.FromText("counter\nabc");
        var session = new CompletionSession();

        session.Typed(document, TextPosition.At(2, 3), () => [Word("abcdef")]);

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void AListWhoseOnlyMatchIsWhatWasTypedStaysShut()
    {
        var document = TextDocument.FromText("x");
        var session = new CompletionSession();

        session.Typed(document, TextPosition.At(1, 1), () => [Word("x")]);

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void TypingMoreNarrowsAndANonIdentifierCharacterCloses()
    {
        var session = new CompletionSession();
        var document = TextDocument.FromText("c");
        session.Typed(document, TextPosition.At(1, 1), () => [Word("counter"), Word("color")]);
        Assert.Equal(2, session.Current!.Items.Count);

        document = TextDocument.FromText("co");
        session.Follow(document, TextPosition.At(1, 2));
        Assert.Equal(2, session.Current!.Items.Count);

        document = TextDocument.FromText("cou");
        session.Follow(document, TextPosition.At(1, 3));
        Assert.Equal(["counter"], Labels(session.Current!.Items));

        document = TextDocument.FromText("cou.");
        session.Follow(document, TextPosition.At(1, 4));
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void MovingTheCaretBeforeTheWordCloses()
    {
        var document = TextDocument.FromText("x c");
        var session = new CompletionSession();
        session.Typed(document, TextPosition.At(1, 3), () => [Word("counter")]);

        session.Follow(document, TextPosition.At(1, 1));

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void TheSelectionWrapsAndAPageStopsAtTheEnd()
    {
        var document = TextDocument.FromText("");
        var session = new CompletionSession();
        session.Invoke(document, TextPosition.At(1, 0), () => [Word("aa"), Word("bb"), Word("cc")]);

        session.Move(-1);
        Assert.Equal(2, session.Current!.Selected);
        session.Move(1);
        Assert.Equal(0, session.Current!.Selected);
        session.Page(10);
        Assert.Equal(2, session.Current!.Selected);
    }

    [Fact]
    public void EnterReplacesThePrefixAndTabTheWholeWord()
    {
        var document = TextDocument.FromText("coXYZ");
        var enter = new CompletionSession();
        enter.Invoke(document, TextPosition.At(1, 2), () => [Word("counter")]);
        var typed = enter.Accept(document, TextPosition.At(1, 2), wholeWord: false)!.Value;
        Assert.Equal(new TextRange(TextPosition.At(1, 0), TextPosition.At(1, 2)), typed.Range);
        Assert.Equal("counter", typed.Text);

        var tab = new CompletionSession();
        tab.Invoke(document, TextPosition.At(1, 2), () => [Word("counter")]);
        var whole = tab.Accept(document, TextPosition.At(1, 2), wholeWord: true)!.Value;
        Assert.Equal(new TextRange(TextPosition.At(1, 0), TextPosition.At(1, 5)), whole.Range);
        Assert.False(tab.IsOpen);
    }

    [Fact]
    public void AnAcceptedCompletionIsOneUndoStepWithTheCaretAfterIt()
    {
        var session = EditorSession.Of("int co");

        var after = session.Complete(
            EditorSession.Caret(1, 6), new TextRange(TextPosition.At(1, 4), TextPosition.At(1, 6)), "counter");

        Assert.Equal("int counter", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 11), after.Caret);
        session.Undo();
        Assert.Equal("int co", session.Document.Text);
    }
}

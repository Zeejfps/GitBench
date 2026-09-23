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

        session.Typed(document, TextPosition.At(2, 1), () => [Word("counter")], asking: false);

        Assert.True(session.IsOpen);
        Assert.Equal("c", session.Current!.Prefix);
    }

    [Fact]
    public void TypingInTheMiddleOfAWordDoesNotOpenTheList()
    {
        var document = TextDocument.FromText("counter\nabc");
        var session = new CompletionSession();

        session.Typed(document, TextPosition.At(2, 3), () => [Word("abcdef")], asking: false);

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void AListWhoseOnlyMatchIsWhatWasTypedStaysShut()
    {
        var document = TextDocument.FromText("x");
        var session = new CompletionSession();

        session.Typed(document, TextPosition.At(1, 1), () => [Word("x")], asking: false);

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void TypingMoreNarrowsAndANonIdentifierCharacterCloses()
    {
        var session = new CompletionSession();
        var document = TextDocument.FromText("c");
        session.Typed(document, TextPosition.At(1, 1), () => [Word("counter"), Word("color")], asking: false);
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
        session.Typed(document, TextPosition.At(1, 3), () => [Word("counter")], asking: false);

        session.Follow(document, TextPosition.At(1, 1));

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void TheSelectionWrapsAndAPageStopsAtTheEnd()
    {
        var document = TextDocument.FromText("");
        var session = new CompletionSession();
        session.Invoke(document, TextPosition.At(1, 0), () => [Word("aa"), Word("bb"), Word("cc")], asking: false);

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
        enter.Invoke(document, TextPosition.At(1, 2), () => [Word("counter")], asking: false);
        var typed = enter.Accept(document, TextPosition.At(1, 2), wholeWord: false)!.Value;
        Assert.Equal(new TextRange(TextPosition.At(1, 0), TextPosition.At(1, 2)), typed.Range);
        Assert.Equal("counter", typed.Text);

        var tab = new CompletionSession();
        tab.Invoke(document, TextPosition.At(1, 2), () => [Word("counter")], asking: false);
        var whole = tab.Accept(document, TextPosition.At(1, 2), wholeWord: true)!.Value;
        Assert.Equal(new TextRange(TextPosition.At(1, 0), TextPosition.At(1, 5)), whole.Range);
        Assert.False(tab.IsOpen);
    }

    private static CompletionItem Served(string label, string text, int insertStart, int? replaceEnd = null,
        params TextEdit[] additional) =>
        new(label, new CompletionKind.Symbol(SymbolKind.Method))
        {
            Insert = new CompletionInsert.ServerEdit(
                text,
                TextPosition.At(1, insertStart),
                replaceEnd is { } end ? TextPosition.At(1, end) : null,
                additional),
        };

    [Fact]
    public void AServerAnswerReplacesWhatTheFileOffered()
    {
        var document = TextDocument.FromText("Wr");
        var session = new CompletionSession();
        session.Invoke(document, TextPosition.At(1, 2), () => [Word("Wrong")], asking: true);

        session.Answered(new FileLine(1), 0, [Served("WriteLine", "WriteLine", 0)], false, document, TextPosition.At(1, 2));

        Assert.Equal(["WriteLine"], Labels(session.Current!.Items));
        Assert.Equal(ServerCompletionState.Answered, session.Current.Server);
    }

    [Fact]
    public void NoServerToAskLeavesTheFilesOwnWords()
    {
        var document = TextDocument.FromText("co");
        var session = new CompletionSession();
        session.Invoke(document, TextPosition.At(1, 2), () => [Word("counter")], asking: true);

        session.Answered(new FileLine(1), 0, null, false, document, TextPosition.At(1, 2));

        Assert.Equal(["counter"], Labels(session.Current!.Items));
        Assert.Equal(ServerCompletionState.None, session.Current.Server);
    }

    [Fact]
    public void AnAnswerForAListThatMovedOnIsIgnored()
    {
        var document = TextDocument.FromText("x co");
        var session = new CompletionSession();
        session.Invoke(document, TextPosition.At(1, 4), () => [Word("counter")], asking: true);

        session.Answered(new FileLine(1), 0, [Served("Other", "Other", 0)], false, document, TextPosition.At(1, 4));

        Assert.Equal(["counter"], Labels(session.Current!.Items));
    }

    [Fact]
    public void AMemberListWaitsEmptyForItsServerAndSurvivesAnEmptyPrefix()
    {
        var document = TextDocument.FromText("Console.");
        var session = new CompletionSession();

        session.OpenForMembers(TextPosition.At(1, 8));
        Assert.True(session.IsOpen);
        Assert.Empty(session.Current!.Items);

        session.Answered(new FileLine(1), 8, [Served("WriteLine", "WriteLine", 8), Served("Beep", "Beep", 8)],
            false, document, TextPosition.At(1, 8));

        Assert.Equal(["Beep", "WriteLine"], Labels(session.Current!.Items).Order());
    }

    [Fact]
    public void AMemberListWithNothingFromItsServerCloses()
    {
        var document = TextDocument.FromText("x.");
        var session = new CompletionSession();
        session.OpenForMembers(TextPosition.At(1, 2));

        session.Answered(new FileLine(1), 2, [], false, document, TextPosition.At(1, 2));

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void AServerEditReachesAsFarAsTheServerSaidOnTab()
    {
        var document = TextDocument.FromText("x.WrXYZ");
        var session = new CompletionSession();
        session.OpenForMembers(TextPosition.At(1, 2));
        session.Answered(new FileLine(1), 2, [Served("WriteLine", "WriteLine", 2, replaceEnd: 7)],
            false, document, TextPosition.At(1, 4));

        var edit = session.Accept(document, TextPosition.At(1, 4), wholeWord: true)!.Value;

        Assert.Equal(new TextRange(TextPosition.At(1, 2), TextPosition.At(1, 7)), edit.Range);
        Assert.Equal("WriteLine", edit.Text);
    }

    [Fact]
    public void AnImportThatComesWithACompletionMovesTheCaretDownWithIt()
    {
        var session = EditorSession.Of("class C\n{\n    Li\n}");
        var import = new TextEdit(TextRange.Caret(TextPosition.At(1, 0)), "using System.Collections.Generic;\n");

        var after = session.Complete(
            EditorSession.Caret(3, 6), new TextRange(TextPosition.At(3, 4), TextPosition.At(3, 6)), "List", [import]);

        Assert.Equal("using System.Collections.Generic;\nclass C\n{\n    List\n}", session.Document.Text);
        Assert.Equal(TextPosition.At(4, 8), after.Caret);
        session.Undo();
        Assert.Equal("class C\n{\n    Li\n}", session.Document.Text);
    }

    [Fact]
    public void TheServersOrderBreaksTiesBetweenEqualMatches()
    {
        var ranked = CompletionMatcher.Rank("", [
            new CompletionItem("zeta", CompletionKind.AWord) { SortText = "0" },
            new CompletionItem("al", CompletionKind.AWord) { SortText = "1" },
        ]);

        Assert.Equal(["zeta", "al"], Labels(ranked));
    }

    [Fact]
    public void AFilterTextIsMatchedWhereTheLabelIsNot()
    {
        var ranked = CompletionMatcher.Rank("wri", [
            new CompletionItem("Console.WriteLine(string)", CompletionKind.AWord) { FilterText = "WriteLine" },
        ]);

        Assert.Single(ranked);
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

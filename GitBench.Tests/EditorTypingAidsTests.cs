using GitBench.Features.Editor;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>What typing a bracket, a quote or a newline does beyond inserting it: pairs, wrapping,
/// stepping over closers, and indentation that follows the blocks it opens and closes.</summary>
public sealed class EditorTypingAidsTests
{
    private static EditSession CSharp(string text, IndentStyle indent = IndentStyle.Spaces) =>
        EditorSession.Of(text, new EditOptions(indent, LineEnding.Lf, "//", TypingRules.For("csharp")));

    private static EditSession Python(string text) =>
        EditorSession.Of(text, new EditOptions(IndentStyle.Spaces, LineEnding.Lf, "#", TypingRules.For("python")));

    [Fact]
    public void AnOpenerClosesItselfWithTheCaretBetween()
    {
        var session = CSharp("");

        var after = session.Type(EditorSession.Caret(1, 0), "(");

        Assert.Equal("()", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void AnOpenerGluedToAWordDoesNotPair()
    {
        var session = CSharp("abc");

        session.Type(EditorSession.Caret(1, 0), "(");

        Assert.Equal("(abc", session.Document.Text);
    }

    [Fact]
    public void TypingTheCloserStepsOverTheOneAlreadyThere()
    {
        var session = CSharp("");

        var caret = session.Type(EditorSession.Caret(1, 0), "(");
        caret = session.Type(caret, "a");
        caret = session.Type(caret, ")");

        Assert.Equal("(a)", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 3), caret.Caret);
        Assert.Equal(1, session.Journal.UndoDepth);
    }

    [Fact]
    public void AQuotePairsButNotAfterALetter()
    {
        var session = CSharp("x = ");

        var caret = session.Type(EditorSession.Caret(1, 4), "\"");
        Assert.Equal("x = \"\"", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 5), caret.Caret);

        var prose = CSharp("don");
        prose.Type(EditorSession.Caret(1, 3), "'");
        Assert.Equal("don'", prose.Document.Text);
    }

    [Fact]
    public void TheClosingQuoteOfAStringIsSteppedOver()
    {
        var session = CSharp("\"abc\"");

        var after = session.Type(EditorSession.Caret(1, 4), "\"");

        Assert.Equal("\"abc\"", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 5), after.Caret);
    }

    [Fact]
    public void NothingPairsInsideALineComment()
    {
        var session = CSharp("// see ");

        session.Type(EditorSession.Caret(1, 7), "(");

        Assert.Equal("// see (", session.Document.Text);
    }

    [Fact]
    public void AnOpenerTypedOverASelectionWrapsIt()
    {
        var session = CSharp("abc");

        var after = session.Type(EditorSession.Select(1, 0, 1, 3), "(");

        Assert.Equal("(abc)", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Anchor);
        Assert.Equal(TextPosition.At(1, 4), after.Caret);

        session.Undo();
        Assert.Equal("abc", session.Document.Text);
    }

    [Fact]
    public void BackspaceInsideAnEmptyPairTakesBothHalves()
    {
        var session = CSharp("f()");

        var after = session.Delete(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("f", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void EnterBetweenBracesPutsTheCloserOnItsOwnLine()
    {
        var session = CSharp("    if (x) {}");

        var after = session.InsertNewline(EditorSession.Caret(1, 12));

        Assert.Equal("    if (x) {\n        \n    }", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 8), after.Caret);

        session.Undo();
        Assert.Equal("    if (x) {}", session.Document.Text);
    }

    [Fact]
    public void EnterAfterAnOpenerIndentsOneLevel()
    {
        var session = CSharp("    void M() {");

        var after = session.InsertNewline(EditorSession.Caret(1, 14));

        Assert.Equal("    void M() {\n        ", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 8), after.Caret);
    }

    [Fact]
    public void EnterIndentsWithTabsInATabIndentedFile()
    {
        var session = CSharp("\t{}", IndentStyle.Tabs);

        session.InsertNewline(EditorSession.Caret(1, 2));

        Assert.Equal("\t{\n\t\t\n\t}", session.Document.Text);
    }

    [Fact]
    public void ABraceInsideAStringOpensNothing()
    {
        var session = CSharp("var s = \"{\"");

        session.InsertNewline(EditorSession.Caret(1, 10));

        Assert.Equal("var s = \"{\n\"", session.Document.Text);
    }

    [Fact]
    public void AColonOpensABlockInPythonButNotInCSharp()
    {
        var python = Python("def f():");
        python.InsertNewline(EditorSession.Caret(1, 8));
        Assert.Equal("def f():\n    ", python.Document.Text);

        var csharp = CSharp("case 1:");
        csharp.InsertNewline(EditorSession.Caret(1, 7));
        Assert.Equal("case 1:\n", csharp.Document.Text);
    }

    [Fact]
    public void ACloserTypedOnABlankLineLinesUpWithItsOpener()
    {
        var session = CSharp("    if (x) {\n        foo();\n        ");

        var after = session.Type(EditorSession.Caret(3, 8), "}");

        Assert.Equal("    if (x) {\n        foo();\n    }", session.Document.Text);
        Assert.Equal(TextPosition.At(3, 5), after.Caret);
    }

    [Fact]
    public void ACloserSkipsBracketsInStringsWhenFindingItsOpener()
    {
        var session = CSharp("{\n    var s = \"{\";\n    ");

        session.Type(EditorSession.Caret(3, 4), "}");

        Assert.Equal("{\n    var s = \"{\";\n}", session.Document.Text);
    }

    [Fact]
    public void AFileOfNoLanguageTypesBracketsAsTheyAre()
    {
        var session = EditorSession.Of("");

        var caret = session.Type(EditorSession.Caret(1, 0), "{");
        session.InsertNewline(caret);

        Assert.Equal("{\n", session.Document.Text);
    }

    [Fact]
    public void RustDoesNotPairTheApostropheOfALifetime()
    {
        var session = EditorSession.Of(
            "fn f(x: &", new EditOptions(IndentStyle.Spaces, LineEnding.Lf, "//", TypingRules.For("rust")));

        session.Type(EditorSession.Caret(1, 9), "'");

        Assert.Equal("fn f(x: &'", session.Document.Text);
    }

    [Theory]
    [InlineData("Max(", 0)]
    [InlineData("Max(1, ", 1)]
    [InlineData("Max(Min(1, 2), ", 1)]
    [InlineData("Max(\"a, b\", ", 1)]
    [InlineData("Max(new[] { 1, 2 }, x, ", 2)]
    public void TheArgumentIsTheCommasAtTheCallsOwnDepth(string before, int expected)
    {
        var rules = TypingRules.For("csharp");

        var index = LineContext.ArgumentIndex(_ => before, 1, before.Length, rules, "//");

        Assert.Equal(expected, index);
    }

    [Fact]
    public void AnArgumentIsCountedAcrossLines()
    {
        string[] lines = ["Call(first,", "     second, "];

        var index = LineContext.ArgumentIndex(n => lines[n - 1], 2, lines[1].Length, TypingRules.For("csharp"), "//");

        Assert.Equal(2, index);
    }

    [Fact]
    public void InsideABraceRatherThanACallThereIsNoArgument() =>
        Assert.Null(LineContext.ArgumentIndex(_ => "var x = new[] { 1, ", 1, 19, TypingRules.For("csharp"), "//"));

    [Fact]
    public void OptionsForASourceFilePickTheLanguagesRules()
    {
        Assert.True(EditOptions.For("Program.cs", LineEnding.Lf, []).Typing.IsQuote('"'));
        Assert.True(EditOptions.For("run.py", LineEnding.Lf, []).Typing.ColonOpensBlock);
        Assert.Same(TypingRules.None, EditOptions.For("notes.unknownext", LineEnding.Lf, []).Typing);
    }
}

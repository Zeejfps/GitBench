using System.Text;

using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Theming;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Pins the incremental parse against the only thing that can judge it: a parse of the same text
/// from scratch.
/// </summary>
/// <remarks>
/// <para>
/// The assertions are on <see cref="TokenSpan"/>s rather than on byte captures, because
/// <see cref="TokenSpan"/> columns are tab-<em>expanded</em>: a byte-level comparison passes on a
/// tab-indented file that renders wrong.
/// </para>
/// <para>
/// None of this says anything about the three point fields on the edit, and nothing can — the
/// parser is handed the whole new buffer and re-derives every row and column from it, so a wrong
/// point produces a byte-identical tree. What is pinned here is the byte arithmetic and the
/// line-index splice, which is where the bugs that show up live.
/// </para>
/// </remarks>
[Collection(nameof(CodeIntelCollection))]
public sealed class DocumentParseTests(CodeIntelFixture fixture)
{
    private const string CSharpId = "csharp";

    private const string Sample = """
        using System;

        namespace Demo;

        internal sealed class Widget
        {
            private readonly int _count;

            public Widget(int count) => _count = count;

            public string Describe() => $"widget of {_count}";
        }
        """;

    // ---- The regression this exists to fix -------------------------------------------------

    [Fact]
    public void TypingAtTheHeadOfALineMovesTheKeywordColouringOntoTheKeyword()
    {
        var document = TextDocument.FromText(Sample);
        using var parse = Open(document);

        var before = SpansOf(parse, line: 1);
        var keyword = Assert.Single(before, span => span.Start == 0);
        Assert.Equal(5, keyword.Length);

        Type(document, parse, TextPosition.At(1, 0), "var q=1; ");

        Assert.Equal("var q=1; using System;", document.Line(new FileLine(1)));

        // The old span covered columns 0..5, and staying there is exactly the bug: `var q=1;`
        // painted as a keyword while the real `using` goes plain.
        var after = SpansOf(parse, line: 1);
        Assert.Contains(after, span => span.Start == 9 && span.Length == 5 && span.Slot == keyword.Slot);
        Assert.DoesNotContain(after, span => span.Start == 0 && span.Length == 5 && span.Slot == keyword.Slot);
    }

    [Fact]
    public void TypingADeclarationGivesItAnOutlineEntry()
    {
        var document = TextDocument.FromText(Sample);
        using var parse = Open(document);

        Assert.DoesNotContain("Added", Names(parse.Read().Outline));

        var end = document.Line(new FileLine(11)).Length;
        Type(document, parse, TextPosition.At(11, end), "\n\n    public int Added() => 1;");

        Assert.Contains("Added", Names(parse.Read().Outline));
    }

    // ---- Equivalence under random edits ----------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryEditLeavesTheSameSpansAndOutlineAFreshParseWouldGive(int seed)
    {
        var random = new Random(seed);
        var document = TextDocument.FromText(Sample);
        using var parse = Open(document);

        for (var step = 0; step < 60; step++)
        {
            var (range, replacement) = RandomEdit(random, document);
            Type(document, parse, range, replacement);

            AssertMatchesAFreshParse($"seed {seed} step {step}", document, parse);
        }

        // Equality would also hold if every edit had been thrown away and the file re-parsed, which
        // is the one way this suite could pass while testing nothing.
        Assert.Equal(0, parse.Fallbacks);
    }

    // ---- The offset mapping ----------------------------------------------------------------

    [Theory]
    // A file whose lines end in CRLF: the document counts one terminator, the parse buffer holds
    // one byte, and the columns either side of that have to agree.
    [InlineData("class A\r\n{\r\n    int x;\r\n}\r\n", 3, 4, 4, "long y; ")]
    // A lone CR ends a line too, in both.
    [InlineData("class A\r{\r    int x;\r}\r", 3, 4, 4, "long y; ")]
    // Non-ASCII before the caret: the column is UTF-16 code units and the offset is bytes.
    [InlineData("// 你好世界\nclass A { int x; }\n", 1, 8, 8, "!")]
    // A surrogate pair is two code units and four bytes.
    [InlineData("// 🚀🚀\nclass A { int x; }\n", 1, 7, 7, "!")]
    // Tabs, whose expanded columns are what the spans are measured in.
    [InlineData("class A\n{\n\t\tint x;\n}\n", 3, 2, 2, "long y; ")]
    // A multi-line insertion.
    [InlineData("class A\n{\n    int x;\n}\n", 3, 4, 4, "int a;\n    int b;\n    ")]
    // An edit at the very end of the file.
    [InlineData("class A\n{\n    int x;\n}\n", 5, 0, 0, "class B { }\n")]
    public void AnEditMapsOntoTheSameTreeAFreshParseWouldBuild(
        string text, int line, int startColumn, int endColumn, string replacement)
    {
        var document = TextDocument.FromText(text);
        using var parse = Open(document);

        Type(
            document,
            parse,
            new TextRange(TextPosition.At(line, startColumn), TextPosition.At(line, endColumn)),
            replacement);

        AssertMatchesAFreshParse("mapped edit", document, parse);
        Assert.Equal(0, parse.Fallbacks);
    }

    [Fact]
    public void DeletingAWholeLineRemovesTheRightEntryFromTheLineIndex()
    {
        var document = TextDocument.FromText("class A\n{\n    int x;\n    int y;\n}\n");
        using var parse = Open(document);

        Type(document, parse, new TextRange(TextPosition.At(3, 0), TextPosition.At(4, 0)), string.Empty);

        Assert.Equal("    int y;", document.Line(new FileLine(3)));
        AssertMatchesAFreshParse("whole-line deletion", document, parse);
        Assert.Equal(0, parse.Fallbacks);
    }

    [Fact]
    public void AnEditWidenedToKeepACrLfWholeStillMaps()
    {
        // Typing a newline immediately after a lone CR: applying it as given would leave the
        // document holding a CRLF split across two pieces, so TextDocument widens the edit to
        // swallow the CR. The mapping has to follow the widened edit, not the one asked for.
        var document = TextDocument.FromText("class A\r{ int x; }\n");
        using var parse = Open(document);

        var applied = document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(2, 0)), "\n"), out var forward);
        Assert.Equal(1, forward.Range.Start.Line.Value);
        Assert.Equal("\r\n", forward.Replacement);

        Assert.True(parse.Follow(applied, document.Slice(applied.Range)));
        AssertMatchesAFreshParse("widened edit", document, parse);
        Assert.Equal(0, parse.Fallbacks);
    }

    [Fact]
    public void AnEmptyFileParsesAndThenFollowsItsFirstKeystroke()
    {
        var document = TextDocument.FromText(string.Empty);
        using var parse = Open(document);

        Assert.Null(parse.Read().Outline);

        Type(document, parse, TextPosition.At(1, 0), "class A { }");

        AssertMatchesAFreshParse("first keystroke", document, parse);
    }

    // ---- A mapping that cannot be followed at all --------------------------------------------

    [Fact]
    public void AnImpossibleEditIsRefusedAndLeavesTheParseAskingToStartOver()
    {
        var document = TextDocument.FromText(Sample);
        using var parse = Open(document);

        // A line the buffer does not have: there is no offset to map it onto, and guessing one
        // would leave the tree describing a file nobody has. The parse says so instead.
        Assert.False(parse.Follow(
            new TextEdit(TextRange.Caret(TextPosition.At(9_000, 0)), "removed"), "put in"));
    }

    // ---- A reload is not an edit -------------------------------------------------------------

    [Fact]
    public void AReloadTakesTheNewTextWholeRatherThanEditingTheTreeWithIt()
    {
        var document = TextDocument.FromText(Sample);
        using var parse = Open(document);
        _ = parse.Read();

        const string reloaded = "internal sealed class Other\n{\n    public int Answer() => 42;\n}\n";
        parse.Reset(reloaded);

        Assert.Contains("Answer", Names(parse.Read().Outline));
        AssertMatchesAFreshParse("after the reload", TextDocument.FromText(reloaded), parse);
        Assert.Equal(0, parse.Fallbacks);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private DocumentParse Open(TextDocument document) => Fresh(document.Text);

    private DocumentParse Fresh(string text) =>
        new(fixture.Highlighter, fixture.Symbols, CSharpId, CodeLanguage.CSharp, text);

    private static void Type(
        TextDocument document, DocumentParse parse, TextPosition at, string replacement) =>
        Type(document, parse, TextRange.Caret(at), replacement);

    private static void Type(
        TextDocument document, DocumentParse parse, TextRange range, string replacement)
    {
        var inverse = document.Apply(new TextEdit(range, replacement));
        Assert.True(parse.Follow(inverse, document.Slice(inverse.Range)));
    }

    private void AssertMatchesAFreshParse(string because, TextDocument document, DocumentParse parse)
    {
        using var fresh = Fresh(document.Text);
        var expected = fresh.Read();
        var actual = parse.Read();

        AssertSameSpans(because, expected, actual, document.LineCount);
        Assert.Equal(Names(expected.Outline), Names(actual.Outline));
    }

    private static void AssertSameSpans(
        string because, EditorAnnotations expected, EditorAnnotations actual, int lineCount)
    {
        Assert.Equal(expected.Highlight is null, actual.Highlight is null);
        if (expected.Highlight is not { } wanted || actual.Highlight is not { } got) return;

        for (var line = 1; line <= lineCount; line++)
        {
            var expectedLine = Describe(wanted.ForLine(DiffLineKind.Context, null, line));
            var actualLine = Describe(got.ForLine(DiffLineKind.Context, null, line));
            Assert.True(
                expectedLine == actualLine,
                $"{because}, line {line}: expected [{expectedLine}], got [{actualLine}].");
        }
    }

    private static string Describe(IReadOnlyList<TokenSpan> spans) =>
        string.Join(' ', spans.Select(s => $"{s.Start}+{s.Length}:{s.Slot}"));

    private static IReadOnlyList<TokenSpan> SpansOf(DocumentParse parse, int line) =>
        parse.Read().Highlight?.ForLine(DiffLineKind.Context, null, line) ?? [];

    private static string Names(FileOutline? outline)
    {
        if (outline is null) return string.Empty;
        var names = new StringBuilder();
        Walk(outline.Roots);
        return names.ToString();

        void Walk(IReadOnlyList<OutlineNode> nodes)
        {
            foreach (var node in nodes)
            {
                names.Append(node.Name).Append('@').Append(node.StartLine).Append(':')
                    .Append(node.EndLine).Append(' ');
                Walk(node.Children);
            }
        }
    }

    private static (TextRange Range, string Replacement) RandomEdit(Random random, TextDocument document)
    {
        var start = RandomPosition(random, document);
        var end = random.Next(3) == 0 ? RandomPosition(random, document) : start;
        var range = end < start ? new TextRange(end, start) : new TextRange(start, end);

        var replacement = random.Next(4) switch
        {
            0 => string.Empty,
            1 => "x",
            2 => "\n    int added;\n",
            _ => "public void Fresh() { }",
        };

        return (range, replacement);
    }

    private static TextPosition RandomPosition(Random random, TextDocument document)
    {
        var line = random.Next(1, document.LineCount + 1);
        var text = document.Line(new FileLine(line));
        return TextPosition.At(line, random.Next(text.Length + 1));
    }
}

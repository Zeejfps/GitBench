using GitBench.Features.Diff;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

// Runs the real TextMateSharp engine in-test (the high-value correctness check: state-stack
// threading across lines, the size cap, and graceful handling of an unknown grammar).
public class SyntaxHighlighterTests
{
    private static readonly SyntaxHighlighter Highlighter = new();

    private static IReadOnlyList<IReadOnlyList<TokenSpan>> HighlightOrFail(string text, string lang)
    {
        var result = Highlighter.Highlight(text, lang);
        Assert.NotNull(result);
        return result!;
    }

    private static bool LineHasSlot(IReadOnlyList<TokenSpan> spans, TokenColorSlot slot)
        => spans.Any(s => s.Slot == slot);

    private static TokenColorSlot SlotAt(IReadOnlyList<TokenSpan> spans, int column)
        => spans.Where(s => column >= s.Start && column < s.Start + s.Length).Select(s => s.Slot).FirstOrDefault();

    [Fact]
    public void MultiLineBlockComment_EveryInteriorLineIsComment()
    {
        var src = string.Join("\n",
            "int x = 1;",
            "/* comment starts here",
            "   still in the comment",
            "   and here too",
            "end */ int y = 2;");
        var lines = HighlightOrFail(src, "csharp");

        // Lines 1..3 (0-based) are entirely inside the block comment — each must carry a
        // Comment span, which only happens if IStateStack is threaded between lines.
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Comment));
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Comment));
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Comment));
    }

    [Fact]
    public void MultiLineVerbatimString_InteriorLineIsString()
    {
        var src = string.Join("\n",
            "var s = @\"line one",
            "line two",
            "line three\";");
        var lines = HighlightOrFail(src, "csharp");

        Assert.True(LineHasSlot(lines[1], TokenColorSlot.String));
    }

    [Fact]
    public void SingleLineConstructs_AreColored()
    {
        var lines = HighlightOrFail("int count = 42; // note", "csharp");
        var spans = lines[0];

        Assert.True(LineHasSlot(spans, TokenColorSlot.Number));   // 42
        Assert.True(LineHasSlot(spans, TokenColorSlot.Comment));  // // note
    }

    [Fact]
    public void TypeScriptString_IsColored()
    {
        var lines = HighlightOrFail("const greeting = \"hello\";", "typescript");
        Assert.True(LineHasSlot(lines[0], TokenColorSlot.String));
    }

    [Fact]
    public void Svelte_MarkupAndEmbeddedScript_AreColored()
    {
        // Svelte isn't a TextMateSharp-bundled grammar — this proves the embedded grammar loads
        // and that its <script lang="ts"> block tokenizes via the bundled TypeScript grammar.
        var src = string.Join("\n",
            "<script lang=\"ts\">",
            "  const greeting: string = \"hello\";",
            "</script>",
            "<h1 class=\"title\">{greeting}</h1>");
        var lines = HighlightOrFail(src, "svelte");

        Assert.True(LineHasSlot(lines[1], TokenColorSlot.String));   // "hello" inside the TS block
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Keyword));  // const
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Keyword));  // <h1> tag name
    }

    [Fact]
    public void Ctx_Constructs_AreColored()
    {
        var src = string.Join("\n",
            "// Bounds-checked pointer to element i.",
            "fn at(T) { s: slice::Slice(T), i: usize } -> *T {",
            "    if i >= s.len { @panic() }",
            "    return slice::get{ s, i = 42 }",
            "}");
        var lines = HighlightOrFail(src, "ctx");

        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Comment));
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Keyword));   // fn
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Function));  // at
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Type));      // Slice, usize
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Function));  // @panic
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Number));    // 42
    }

    [Fact]
    public void Ctx_StdConstructs_AreColored()
    {
        var src = string.Join("\n",
            "const F64_MAX: f64 = 1.7976931348623157e308",
            "defer list::free{ list = &events, .. }",
            "list::push{ list = &events, item = Event::push{ value = i }, .. }",
            "        full{ &v } => { return &v }",
            "let nl = '\\x0a'");
        var lines = HighlightOrFail(src, "ctx");

        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Keyword));   // const
        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Constant));  // F64_MAX
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Keyword));   // defer
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Variable));  // list =
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Constant));  // Event::push variant
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Constant));  // full arm
        Assert.True(LineHasSlot(lines[4], TokenColorSlot.String));    // '\x0a'
    }

    [Fact]
    public void Ctx_ControlFlow_AreColored()
    {
        var src = string.Join("\n",
            "while done {",
            "    match t {",
            "        _ = f{ n = x << 2 } /* shifted */",
            "    if done { break } else { continue }");
        var lines = HighlightOrFail(src, "ctx");

        Assert.False(LineHasSlot(lines[0], TokenColorSlot.Function)); // bare condition, not a call
        Assert.False(LineHasSlot(lines[1], TokenColorSlot.Function)); // bare scrutinee, not a call
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Function));  // f{
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Comment));   // /* */
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Keyword));   // break, continue
    }

    [Fact]
    public void Ctx_Enum_AreColored()
    {
        var src = string.Join("\n",
            "enum Kind: u8 {",
            "    ident,",
            "    lbrace = 40,",
            "}",
            "let k = Kind::lbrace");
        var lines = HighlightOrFail(src, "ctx");

        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Keyword));   // enum
        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Type));      // Kind, u8
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Constant));  // ident
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Constant));  // lbrace
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Number));    // 40
        Assert.True(LineHasSlot(lines[4], TokenColorSlot.Constant));  // Kind::lbrace
    }

    [Fact]
    public void Ctx_ExternAndCapabilities_AreColored()
    {
        var src = string.Join("\n",
            "capability Glfw",
            "#c::symbol{ name = \"labs\" }",
            "extern fn long_abs { mut glfw: Glfw, n: i64 } -> i64",
            "#inline");
        var lines = HighlightOrFail(src, "ctx");

        Assert.Equal(TokenColorSlot.Keyword, SlotAt(lines[0], 0));    // capability
        Assert.Equal(TokenColorSlot.Type, SlotAt(lines[0], 11));      // Glfw
        Assert.Equal(TokenColorSlot.Function, SlotAt(lines[1], 4));   // symbol
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.String));    // "labs"
        Assert.Equal(TokenColorSlot.Keyword, SlotAt(lines[2], 0));    // extern
        Assert.Equal(TokenColorSlot.Function, SlotAt(lines[2], 10));  // long_abs
        Assert.Equal(TokenColorSlot.Function, SlotAt(lines[3], 1));   // inline
    }

    [Fact]
    public void Ctx_OffSpecPrimitives_AreNotTypes()
    {
        var lines = HighlightOrFail("let a = i128", "ctx");

        Assert.False(LineHasSlot(lines[0], TokenColorSlot.Type));
    }

    [Fact]
    public void Markdown_Constructs_AreColored()
    {
        // Markdown is a TextMateSharp-bundled grammar — this proves "markdown" resolves and that
        // its markup.* scopes reach the new prose slots (Heading/Emphasis/Code/Link/Quote).
        var src = string.Join("\n",
            "# Title",
            "Some **bold** and `code` here.",
            "> a quoted line",
            "[label](https://example.com)");
        var lines = HighlightOrFail(src, "markdown");

        Assert.True(LineHasSlot(lines[0], TokenColorSlot.Heading));   // # Title
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Emphasis));  // **bold**
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Code));      // `code`
        Assert.True(LineHasSlot(lines[2], TokenColorSlot.Quote));     // > a quoted line
        Assert.True(LineHasSlot(lines[3], TokenColorSlot.Link));      // the URL
    }

    [Fact]
    public void Markdown_FencedCodeBlock_TokenizesEmbeddedLanguage()
    {
        // A ```cs fence embeds the C# grammar, so an interior keyword colors as code, not prose.
        var src = string.Join("\n",
            "```cs",
            "var x = 1;",
            "```");
        var lines = HighlightOrFail(src, "markdown");
        Assert.True(LineHasSlot(lines[1], TokenColorSlot.Keyword));   // var, via embedded C#
    }

    [Fact]
    public void SpansAreOrderedAndNonOverlapping()
    {
        var lines = HighlightOrFail("int count = 42; // note", "csharp");
        foreach (var line in lines)
        {
            var prevEnd = 0;
            foreach (var span in line)
            {
                Assert.True(span.Start >= prevEnd, $"span at {span.Start} overlaps previous end {prevEnd}");
                Assert.True(span.Length > 0);
                prevEnd = span.Start + span.Length;
            }
        }
    }

    [Fact]
    public void TabsAreExpandedBeforeColumnsAreReported()
    {
        // A leading tab expands to DiffOptions.TabWidth spaces, so the keyword "int" starts at
        // that column — proving spans are in tab-expanded space (aligned with rendered text).
        var lines = HighlightOrFail("\tint x = 1;", "csharp");
        var first = lines[0].FirstOrDefault(s => s.Slot == TokenColorSlot.Keyword);
        Assert.Equal(DiffOptions.TabWidth, first.Start);
    }

    [Fact]
    public void OverSizeFile_ReturnsNull()
    {
        var huge = new string('x', SyntaxHighlighter.MaxFileChars + 1);
        Assert.Null(Highlighter.Highlight(huge, "csharp"));
    }

    [Fact]
    public void UnknownGrammar_ReturnsNull()
        => Assert.Null(Highlighter.Highlight("int x = 1;", "no-such-language"));

    [Fact]
    public void EveryRegisteredLanguage_ResolvesToAGrammar()
    {
        foreach (var id in LanguageRegistry.AllLanguageIds)
            Assert.True(Highlighter.Highlight("x", id) is not null, $"language id '{id}' did not resolve to a grammar");
    }

    [Fact]
    public void LineCountMatchesSourceLines()
    {
        var src = "a\nb\nc"; // 3 lines, no trailing newline
        var lines = HighlightOrFail(src, "csharp");
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void TrailingNewlineYieldsTrailingLine()
    {
        var src = "a\nb\n"; // trailing newline → 3 elements, last empty (keeps 1-based indexing aligned)
        var lines = HighlightOrFail(src, "csharp");
        Assert.Equal(3, lines.Count);
    }
}

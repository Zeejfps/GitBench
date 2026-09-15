using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Theming;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// One highlighted snippet per language the app routes to tree-sitter. Not exhaustive — the point
/// is that each query compiled against its grammar and still names the nodes it thinks it does, so
/// a pin bump that renames one fails here rather than quietly returning a file with no colors.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class TreeSitterHighlightTests(CodeIntelFixture fixture)
{
    // language, source, and the slot each named token must resolve to.
    private static readonly (CodeLanguage Language, string Source, (string Token, TokenColorSlot Slot)[] Expected)[] Cases =
    [
        (CodeLanguage.CSharp, "class Box { void Run() { Helper(1); } }",
            [("class", TokenColorSlot.Keyword), ("Box", TokenColorSlot.Type),
             ("Run", TokenColorSlot.Function), ("Helper", TokenColorSlot.Function),
             ("1", TokenColorSlot.Number)]),

        (CodeLanguage.TypeScript, "function load(): Pair { return make(); }",
            [("function", TokenColorSlot.Keyword), ("load", TokenColorSlot.Function),
             ("Pair", TokenColorSlot.Type), ("make", TokenColorSlot.Function)]),

        (CodeLanguage.Tsx, "const el = <Row n={1} />;",
            [("const", TokenColorSlot.Keyword), ("1", TokenColorSlot.Number)]),

        (CodeLanguage.JavaScript, "function go() { return \"s\"; }",
            [("function", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("\"s\"", TokenColorSlot.String)]),

        (CodeLanguage.Json, "{ \"a\": 12 }", [("12", TokenColorSlot.Number)]),

        (CodeLanguage.Css, ".card { color: red; }", [("color", TokenColorSlot.Variable)]),

        (CodeLanguage.Yaml, "key: value", [("key", TokenColorSlot.Variable)]),

        (CodeLanguage.Python, "def go(x):\n    return \"s\"",
            [("def", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("\"s\"", TokenColorSlot.String)]),

        (CodeLanguage.Go, "func Go() int { return 1 }",
            [("func", TokenColorSlot.Keyword), ("Go", TokenColorSlot.Function),
             ("int", TokenColorSlot.Type), ("1", TokenColorSlot.Number)]),

        (CodeLanguage.Rust, "fn go() -> u32 { 1 }",
            [("fn", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("u32", TokenColorSlot.Type), ("1", TokenColorSlot.Number)]),

        (CodeLanguage.Java, "class A { void go() { } }",
            [("class", TokenColorSlot.Keyword), ("A", TokenColorSlot.Type),
             ("go", TokenColorSlot.Function)]),

        (CodeLanguage.Bash, "echo \"hi\"", [("\"hi\"", TokenColorSlot.String)]),

        (CodeLanguage.C, "int go(void) { return 0; }",
            [("go", TokenColorSlot.Function), ("0", TokenColorSlot.Number)]),

        (CodeLanguage.Markdown, "# Title\n\nA `span` of code.\n",
            [("Title", TokenColorSlot.Heading), ("`span`", TokenColorSlot.Code)]),

        (CodeLanguage.Html, "<p class=\"a\">hi</p>",
            [("p", TokenColorSlot.Keyword), ("class", TokenColorSlot.Variable)]),

        // The markup half comes from HTML's query and the template half from Svelte's own; the
        // bodies and expressions between them are injections, covered in TreeSitterInjectionTests.
        (CodeLanguage.Svelte, "{#if shown}<p id=\"a\">hi</p>{:else}<b>no</b>{/if}\n{#each rows as row}{row}{/each}",
            [("if", TokenColorSlot.Keyword), ("else", TokenColorSlot.Keyword),
             ("each", TokenColorSlot.Keyword), ("as", TokenColorSlot.Keyword),
             ("p", TokenColorSlot.Keyword), ("id", TokenColorSlot.Variable),
             ("{#", TokenColorSlot.Punctuation)]),

        // The table header is a type and the key under it a property, which is the local edit
        // to the vendored query: upstream paints both with the same catch-all.
        (CodeLanguage.Toml, "[package]\nname = \"gitbench\"\nedition = 2021\n",
            [("package", TokenColorSlot.Type), ("name", TokenColorSlot.Variable),
             ("\"gitbench\"", TokenColorSlot.String), ("2021", TokenColorSlot.Number)]),
    ];

    /// <summary>
    /// The canary for a pin bump: a query whose node names the grammar no longer has fails to
    /// compile, the language drops out of <see cref="TreeSitterSyntaxHighlighter.Supports"/>, and
    /// highlighting silently falls back to TextMate with nothing anywhere saying why.
    /// </summary>
    [Fact]
    public void EveryRoutedLanguageCompiledItsQuery()
    {
        var missing = Cases
            .Select(c => c.Language)
            .Where(l => !fixture.Highlighter.Supports(l))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryBundledGrammarWithAHighlightQueryIsCoveredHere()
    {
        // A new highlight query added to Assets/Queries/Highlights without a case here would ship
        // uncovered, which is the state this whole file exists to prevent.
        var covered = Cases.Select(c => c.Language).ToHashSet();
        var routed = CodeLanguages.All.Where(fixture.Highlighter.Supports).ToArray();

        Assert.Empty(routed.Where(l => !covered.Contains(l)));
    }

    // One test rather than a Theory: TokenColorSlot is internal, so it cannot cross a public
    // MemberData boundary. Every mismatch is reported together instead of one failing run at a time.
    [Fact]
    public void EachLanguageColorsWhatItsGrammarUnderstands()
    {
        var wrong = new List<string>();

        foreach (var (language, source, expected) in Cases)
        {
            var spans = fixture.Highlighter.Highlight(source, language);
            if (spans is null)
            {
                wrong.Add($"{language}: highlighted nothing at all");
                continue;
            }

            foreach (var (token, slot) in expected)
            {
                var actual = SlotOf(source, spans, token);
                if (actual != slot) wrong.Add($"{language}: '{token}' was {actual}, expected {slot}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void SpansAreOrderedNonOverlappingAndInsideTheirLine()
    {
        // Every shape whose spans could plausibly overlap or run past a line: nesting, a construct
        // that spans lines, tabs, a non-ASCII literal (byte offsets stop indexing characters), and
        // an escape inside a string.
        var source = string.Join("\n", [
            "using System;",
            "",
            "namespace Acme;",
            "",
            "/// <summary>Doc comment with <c>markup</c>.</summary>",
            "[Obsolete(\"gone\")]",
            "public sealed class Box<T> : IDisposable where T : notnull",
            "{",
            "\tprivate readonly Dictionary<string, List<int>> _items = new();",
            "",
            "\t/* a block comment",
            "\t   spanning lines */",
            "\tpublic string Name { get; init; } = \"héllo \\\" wörld 🎉\";",
            "",
            "\tpublic int Run(int n) => n switch { > 0 => Compute(n), _ => 0 };",
            "",
            "\tprivate static int Compute(int n) => n * 2;",
            "",
            "\tpublic void Dispose() { }",
            "}",
        ]);

        var spans = fixture.Highlighter.Highlight(source, CodeLanguage.CSharp);
        Assert.NotNull(spans);

        var lines = source.Split('\n');
        Assert.Equal(lines.Length, spans.Count);

        for (var i = 0; i < lines.Length; i++)
        {
            var width = DiffText.ExpandTabs(lines[i]).Length;
            var previousEnd = 0;

            foreach (var span in spans[i])
            {
                Assert.True(span.Length > 0, $"line {i + 1}: zero-length span");
                Assert.True(span.Start >= previousEnd, $"line {i + 1}: spans out of order or overlapping");
                Assert.True(span.Start + span.Length <= width, $"line {i + 1}: span runs past the line");
                previousEnd = span.Start + span.Length;
            }
        }
    }

    /// <summary>Columns are tab-expanded, the same space the renderer draws in — a span measured
    /// against the raw line would slide off its glyphs on any indented file.</summary>
    [Fact]
    public void ColumnsAreTabExpanded()
    {
        var spans = fixture.Highlighter.Highlight("class A {\n\tvoid Go() { }\n}", CodeLanguage.CSharp);
        Assert.NotNull(spans);

        var onVoid = Assert.Single(spans[1].Where(s => s.Slot == TokenColorSlot.Type));
        Assert.Equal(DiffOptions.TabWidth, onVoid.Start);
    }

    [Fact]
    public void AFileOverTheCapIsDeclinedRatherThanTruncated()
    {
        var huge = new string('a', ParseText.MaxFileBytes + 1);
        Assert.Null(fixture.Highlighter.Highlight(huge, CodeLanguage.CSharp));
    }

    [Fact]
    public void ALanguageWithNoBundledGrammarNeverReachesTheParser()
    {
        // jsonc is the one id deliberately kept off a grammar we do bundle: the JSON parser reads
        // its comments as errors, so those files are better colored by TextMate.
        Assert.Equal(new FileLanguage.TextMate("fsharp"), FileLanguage.Detect("a.fs"));
        Assert.Equal(new FileLanguage.TextMate("jsonc"), FileLanguage.Detect("a.jsonc"));
        Assert.Equal(new FileLanguage.TextMate("jsonc"), FileLanguage.Detect("tsconfig.json"));
        Assert.Equal(new FileLanguage.TreeSitter(CodeLanguage.Json), FileLanguage.Detect("package.json"));
        Assert.Equal(new FileLanguage.TreeSitter(CodeLanguage.C), FileLanguage.Detect("a.h"));
        Assert.Equal(new FileLanguage.TreeSitter(CodeLanguage.Bash), FileLanguage.Named("sh"));
        Assert.Equal(new FileLanguage.TextMate("fsharp"), FileLanguage.Named("fsharp"));
        Assert.Equal(FileLanguage.None.Instance, FileLanguage.Detect("a.unknown"));
    }

    /// <summary>A file with a syntax error still colors — the reason a parser is usable on a diff
    /// side at all, where half a rename or a conflict marker is ordinary.</summary>
    [Fact]
    public void ABrokenFileStillColorsWhatItCan()
    {
        var spans = fixture.Highlighter.Highlight("class Box { void Run( { \"s\"", CodeLanguage.CSharp);
        Assert.NotNull(spans);
        Assert.Equal(TokenColorSlot.Keyword, SlotOf("class Box { void Run( { \"s\"", spans, "class"));
    }

    internal static TokenColorSlot SlotOf(
        string source,
        IReadOnlyList<IReadOnlyList<TokenSpan>> spans,
        string token)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var expanded = DiffText.ExpandTabs(lines[i]);
            var at = expanded.IndexOf(token, StringComparison.Ordinal);
            if (at < 0) continue;

            var slots = new TokenColorSlot[expanded.Length];
            foreach (var span in spans[i])
            {
                for (var c = span.Start; c < span.Start + span.Length && c < slots.Length; c++)
                {
                    slots[c] = span.Slot;
                }
            }

            var first = slots[at];
            for (var c = at; c < at + token.Length; c++)
            {
                Assert.True(slots[c] == first, $"'{token}' is not one solid run: {slots[c]} at offset {c - at}");
            }

            return first;
        }

        throw new InvalidOperationException($"'{token}' is not in the source.");
    }
}

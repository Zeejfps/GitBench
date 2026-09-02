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
public sealed class TreeSitterHighlightFixture : IDisposable
{
    private readonly TreeSitterSyntaxHighlighter _highlighter = new();

    internal TreeSitterSyntaxHighlighter Highlighter => _highlighter;

    public void Dispose() => _highlighter.Dispose();
}

[CollectionDefinition(nameof(TreeSitterHighlightCollection))]
public sealed class TreeSitterHighlightCollection : ICollectionFixture<TreeSitterHighlightFixture>;

[Collection(nameof(TreeSitterHighlightCollection))]
public class TreeSitterHighlightTests(TreeSitterHighlightFixture fixture)
{
    // languageId, source, and the slot each named token must resolve to.
    private static readonly (string LanguageId, string Source, (string Token, TokenColorSlot Slot)[] Expected)[] Cases =
    [
        ("csharp", "class Box { void Run() { Helper(1); } }",
            [("class", TokenColorSlot.Keyword), ("Box", TokenColorSlot.Type),
             ("Run", TokenColorSlot.Function), ("Helper", TokenColorSlot.Function),
             ("1", TokenColorSlot.Number)]),

        ("typescript", "function load(): Pair { return make(); }",
            [("function", TokenColorSlot.Keyword), ("load", TokenColorSlot.Function),
             ("Pair", TokenColorSlot.Type), ("make", TokenColorSlot.Function)]),

        ("typescriptreact", "const el = <Row n={1} />;",
            [("const", TokenColorSlot.Keyword), ("1", TokenColorSlot.Number)]),

        ("javascript", "function go() { return \"s\"; }",
            [("function", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("\"s\"", TokenColorSlot.String)]),

        ("json", "{ \"a\": 12 }", [("12", TokenColorSlot.Number)]),

        ("css", ".card { color: red; }", [("color", TokenColorSlot.Variable)]),

        ("yaml", "key: value", [("key", TokenColorSlot.Variable)]),

        ("python", "def go(x):\n    return \"s\"",
            [("def", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("\"s\"", TokenColorSlot.String)]),

        ("go", "func Go() int { return 1 }",
            [("func", TokenColorSlot.Keyword), ("Go", TokenColorSlot.Function),
             ("int", TokenColorSlot.Type), ("1", TokenColorSlot.Number)]),

        ("rust", "fn go() -> u32 { 1 }",
            [("fn", TokenColorSlot.Keyword), ("go", TokenColorSlot.Function),
             ("u32", TokenColorSlot.Type), ("1", TokenColorSlot.Number)]),

        ("java", "class A { void go() { } }",
            [("class", TokenColorSlot.Keyword), ("A", TokenColorSlot.Type),
             ("go", TokenColorSlot.Function)]),

        ("shellscript", "echo \"hi\"", [("\"hi\"", TokenColorSlot.String)]),

        ("c", "int go(void) { return 0; }",
            [("go", TokenColorSlot.Function), ("0", TokenColorSlot.Number)]),

        ("markdown", "# Title\n\nA `span` of code.\n",
            [("Title", TokenColorSlot.Heading), ("`span`", TokenColorSlot.Code)]),

        ("html", "<p class=\"a\">hi</p>",
            [("p", TokenColorSlot.Keyword), ("class", TokenColorSlot.Variable)]),

        // The table header is a type and the key under it a property, which is the local edit
        // to the vendored query: upstream paints both with the same catch-all.
        ("toml", "[package]\nname = \"gitbench\"\nedition = 2021\n",
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
            .Select(c => c.LanguageId)
            .Where(id => !fixture.Highlighter.Supports(id))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryBundledGrammarWithAHighlightQueryIsCoveredHere()
    {
        // A new highlight query added to Assets/Queries/Highlights without a case here would ship
        // uncovered, which is the state this whole file exists to prevent.
        var covered = Cases.Select(c => c.LanguageId).ToHashSet();
        var routed = CodeLanguages.All
            .Where(l => fixture.Highlighter.Supports(LanguageIdFor(l) ?? "none"))
            .Select(l => LanguageIdFor(l)!)
            .ToArray();

        Assert.Empty(routed.Where(id => !covered.Contains(id)));
    }

    // One test rather than a Theory: TokenColorSlot is internal, so it cannot cross a public
    // MemberData boundary. Every mismatch is reported together instead of one failing run at a time.
    [Fact]
    public void EachLanguageColorsWhatItsGrammarUnderstands()
    {
        var wrong = new List<string>();

        foreach (var (languageId, source, expected) in Cases)
        {
            var spans = fixture.Highlighter.Highlight(source, languageId);
            if (spans is null)
            {
                wrong.Add($"{languageId}: highlighted nothing at all");
                continue;
            }

            foreach (var (token, slot) in expected)
            {
                var actual = SlotOf(source, spans, token);
                if (actual != slot) wrong.Add($"{languageId}: '{token}' was {actual}, expected {slot}");
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

        var spans = fixture.Highlighter.Highlight(source, "csharp");
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
        var spans = fixture.Highlighter.Highlight("class A {\n\tvoid Go() { }\n}", "csharp");
        Assert.NotNull(spans);

        var onVoid = Assert.Single(spans[1].Where(s => s.Slot == TokenColorSlot.Type));
        Assert.Equal(DiffOptions.TabWidth, onVoid.Start);
    }

    [Fact]
    public void AFileOverTheCapIsDeclinedRatherThanTruncated()
    {
        var huge = new string('a', TreeSitterSyntaxHighlighter.MaxFileBytes + 1);
        Assert.Null(fixture.Highlighter.Highlight(huge, "csharp"));
    }

    [Fact]
    public void ALanguageWithNoBundledQueryIsDeclined()
    {
        // Shipping no highlights query is the whole of the routing rule, and jsonc is the one id
        // deliberately left off a grammar we do bundle: the JSON parser reads its comments as
        // errors, so its files are better colored by TextMate.
        Assert.False(fixture.Highlighter.Supports("fsharp"));
        Assert.False(fixture.Highlighter.Supports("jsonc"));
        Assert.Null(fixture.Highlighter.Highlight("let x = 1", "fsharp"));
    }

    /// <summary>A file with a syntax error still colors — the reason a parser is usable on a diff
    /// side at all, where half a rename or a conflict marker is ordinary.</summary>
    [Fact]
    public void ABrokenFileStillColorsWhatItCan()
    {
        var spans = fixture.Highlighter.Highlight("class Box { void Run( { \"s\"", "csharp");
        Assert.NotNull(spans);
        Assert.Equal(TokenColorSlot.Keyword, SlotOf("class Box { void Run( { \"s\"", spans, "class"));
    }

    private static string? LanguageIdFor(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => "csharp",
        CodeLanguage.TypeScript => "typescript",
        CodeLanguage.Tsx => "typescriptreact",
        CodeLanguage.JavaScript => "javascript",
        CodeLanguage.Json => "json",
        CodeLanguage.Css => "css",
        CodeLanguage.Html => "html",
        CodeLanguage.Markdown => "markdown",
        CodeLanguage.Yaml => "yaml",
        CodeLanguage.Python => "python",
        CodeLanguage.Go => "go",
        CodeLanguage.Rust => "rust",
        CodeLanguage.Java => "java",
        CodeLanguage.Bash => "shellscript",
        CodeLanguage.C => "c",
        CodeLanguage.Toml => "toml",
        _ => null,
    };

    /// <summary>The slot covering <paramref name="token"/>'s first occurrence, or Default where the
    /// token is uncolored. Fails if the token is not one solid run.</summary>
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

using System.Text;

using GitBench.Features.CodeIntel;

using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public class SymbolExtractorTests(CodeIntelFixture fixture)
{
    [Fact]
    public void AvailabilityIsReadyWhenEveryBundledQueryLoads()
    {
        Assert.IsType<CodeIntelAvailability.Ready>(fixture.Extractor.Availability);
    }

    // Per language, deliberately. A query failure now takes down only its own language, so asking
    // whether the extractor is Ready would pass with fourteen of fifteen broken.
    [Fact]
    public void EveryBundledQueryCompilesAndEveryCaptureNameValidates()
    {
        using var extractor = new TreeSitterSymbolExtractor();
        Assert.IsType<CodeIntelAvailability.Ready>(extractor.Availability);

        var missing = CodeLanguages.All.Where(l => !extractor.Supports(l)).ToArray();
        Assert.Empty(missing);

        foreach (var language in CodeLanguages.All)
        {
            Assert.NotEmpty(TreeSitterSymbolExtractor.ReadEmbeddedQuery(language));
        }
    }

    [Fact]
    public void EveryEmbeddedQueryResourceBelongsToACodeLanguage()
    {
        var embedded = typeof(TreeSitterSymbolExtractor).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".scm", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(embedded);

        // Outline queries: every bundled language has one, and nothing else is in that folder.
        var outlines = CodeLanguages.All.Select(l => l.QueryResourceName());
        Assert.Equal(
            outlines.OrderBy(n => n, StringComparer.Ordinal),
            embedded.Where(n => !n.StartsWith("highlights.", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal));

        // Highlight queries: a language may have none — that absence is what routes Markdown and
        // HTML to TextMate — but a file that matches no language at all would embed and never load.
        var known = CodeLanguages.All.Select(l => l.HighlightQueryResourceName()).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(embedded
            .Where(n => n.StartsWith("highlights.", StringComparison.Ordinal))
            .Where(n => !known.Contains(n)));
    }

    [Fact]
    public void AnUnknownCaptureKindMakesTheExtractorUnavailable()
    {
        using var extractor = new TreeSitterSymbolExtractor(
            queryText: _ => "(class_declaration name: (identifier) @name) @def.frobnicate");

        var unavailable = Assert.IsType<CodeIntelAvailability.Unavailable>(extractor.Availability);
        Assert.Contains("frobnicate", unavailable.Reason, StringComparison.Ordinal);
        Assert.Null(extractor.Extract("class A { }", CodeLanguage.CSharp));
    }

    [Fact]
    public void ACaptureOutsideTheProtocolMakesTheExtractorUnavailable()
    {
        using var extractor = new TreeSitterSymbolExtractor(
            queryText: _ => "(class_declaration name: (identifier) @name (declaration_list) @nmae) @def.class");

        var unavailable = Assert.IsType<CodeIntelAvailability.Unavailable>(extractor.Availability);
        Assert.Contains("nmae", unavailable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedQueryMakesTheExtractorUnavailableRatherThanThrowing()
    {
        using var extractor = new TreeSitterSymbolExtractor(queryText: _ => "(no_such_node) @def.class");

        Assert.IsType<CodeIntelAvailability.Unavailable>(extractor.Availability);
    }

    [Fact]
    public void AQueryWithNoDefinitionCaptureIsRejected()
    {
        using var extractor = new TreeSitterSymbolExtractor(
            queryText: _ => "(class_declaration name: (identifier) @name)");

        Assert.IsType<CodeIntelAvailability.Unavailable>(extractor.Availability);
    }

    [Fact]
    public void AFileScopedNamespaceContainsTheTypesBelowIt()
    {
        var outline = fixture.Outline(
            """
            namespace Acme;

            class Widget
            {
                void Resize() { }
            }
            """);

        var ns = Assert.Single(outline.Roots);
        Assert.Equal(SymbolKind.Namespace, ns.Kind);
        Assert.Equal("Acme", ns.Name);
        Assert.Equal(1, ns.StartLine);
        Assert.Equal(1, ns.EndLine);

        var widget = Assert.Single(ns.Children);
        Assert.Equal("Widget", widget.Name);
        Assert.Equal("Resize", Assert.Single(widget.Children).Name);
    }

    [Fact]
    public void ABlockNamespaceContainsTheTypesInsideIt()
    {
        var outline = fixture.Outline(
            """
            namespace Acme
            {
                class Widget { }
            }
            """);

        var ns = Assert.Single(outline.Roots);
        Assert.Equal(1, ns.StartLine);
        Assert.Equal(4, ns.EndLine);
        Assert.Equal("Widget", Assert.Single(ns.Children).Name);
    }

    [Fact]
    public void StartLineSkipsLeadingAttributes()
    {
        var outline = fixture.Outline(
            """
            [Obsolete]
            [Serializable]
            class Widget
            {
                [Obsolete]
                void Resize() { }
            }
            """);

        var widget = Assert.Single(outline.Roots);
        Assert.Equal(3, widget.StartLine);

        var resize = Assert.Single(widget.Children);
        Assert.Equal(6, resize.StartLine);
    }

    [Theory]
    [InlineData("interface IWidget\n{\n    void Resize();\n}", "Resize")]
    [InlineData("abstract class W\n{\n    public abstract void Resize();\n}", "Resize")]
    [InlineData("record Point(int X, int Y);", "Point")]
    [InlineData("delegate void Resized(int width);", "Resized")]
    [InlineData("class W\n{\n    int Width => 1;\n}", "Width")]
    [InlineData("class W\n{\n    int _width;\n}", "_width")]
    [InlineData("enum E\n{\n    None,\n}", "None")]
    [InlineData("partial class W\n{\n    partial void OnChanged();\n}", "OnChanged")]
    public void SignatureEndLineEqualsEndLineWhenThereIsNoBody(string source, string name)
    {
        var node = Find(fixture.Outline(source), name);
        Assert.Equal(node.EndLine, node.SignatureEndLine);
    }

    [Fact]
    public void SignatureEndLineMarksTheBodyLineWhenThereIsOne()
    {
        var outline = fixture.Outline(
            """
            class Widget
            {
                void Resize(
                    int width)
                {
                    _ = width;
                }
            }
            """);

        var resize = Find(outline, "Resize");
        Assert.Equal(3, resize.StartLine);
        Assert.Equal(7, resize.EndLine);
        Assert.Equal(5, resize.SignatureEndLine);
        Assert.True(resize.SignatureEndLine < resize.EndLine);
    }

    [Fact]
    public void ParameterTypesDistinguishOverloads()
    {
        var outline = fixture.Outline(
            """
            class AuthService
            {
                public bool Login() => true;
                public bool Login(string user) => true;
                public bool Login(string user, int attempt) => true;
            }
            """);

        var overloads = Assert.Single(outline.Roots).Children;
        Assert.Equal(["", "string", "string, int"], overloads.Select(n => n.ParameterTypes));
        Assert.Equal(3, overloads.Select(n => (n.Kind, n.Name, n.ParameterTypes)).Distinct().Count());
    }

    [Fact]
    public void ParameterTypesDropNamesAndModifiers()
    {
        var outline = fixture.Outline(
            """
            class W
            {
                void Copy(ref int source, out string result, params int[] rest)
                {
                    result = "";
                }
            }
            """);

        Assert.Equal("int, string, int[]", Find(outline, "Copy").ParameterTypes);
    }

    [Fact]
    public void ParameterTypesCollapseAMultiLineType()
    {
        var outline = fixture.Outline(
            """
            class W
            {
                void Take(Dictionary<string,
                    int> map)
                {
                }
            }
            """);

        Assert.Equal("Dictionary<string, int>", Find(outline, "Take").ParameterTypes);
    }

    [Fact]
    public void ParameterTypesAreNullForDeclarationsThatTakeNone()
    {
        var outline = fixture.Outline(
            """
            class Widget
            {
                int _width;

                int Width => _width;
            }
            """);

        Assert.Null(Find(outline, "Widget").ParameterTypes);
        Assert.Null(Find(outline, "_width").ParameterTypes);
        Assert.Null(Find(outline, "Width").ParameterTypes);
    }

    [Fact]
    public void OverlappingPatternsProduceOneNodePerDeclaration()
    {
        var outline = fixture.Outline(
            """
            class Widget
            {
                private int _width = 5;

                public int Height { get; set; } = 6;
            }
            """);

        var members = Assert.Single(outline.Roots).Children;
        Assert.Equal(["_width", "Height"], members.Select(n => n.Name));
        Assert.Equal(1, members.Count(n => n.Name == "_width"));
    }

    [Fact]
    public void TheFirstBodyCaptureWins()
    {
        var outline = fixture.Outline(
            """
            class Widget
            {
                public int Height { get; set; }
                    = 6;
            }
            """);

        var height = Find(outline, "Height");
        Assert.Equal(3, height.SignatureEndLine);
        Assert.Equal(4, height.EndLine);
    }

    [Fact]
    public void OperatorsAreNamedByTheTokenTheyOverload()
    {
        var outline = fixture.Outline(
            """
            class Money
            {
                public static bool operator ==(Money? a, Money? b) => true;
                public static bool operator !=(Money? a, Money? b) => false;
                public static Money operator +(Money a, Money b) => a;
            }
            """);

        var operators = Assert.Single(outline.Roots).Children;
        Assert.Equal(["==", "!=", "+"], operators.Select(n => n.Name));
        Assert.All(operators, o => Assert.Equal(SymbolKind.Method, o.Kind));
    }

    [Fact]
    public void AConversionOperatorIsNamedByTheTypeItConvertsTo()
    {
        var outline = fixture.Outline(
            """
            class Money
            {
                public static implicit operator decimal(Money m) => 0m;
                public static explicit operator string(Money m) => "";
            }
            """);

        var conversions = Assert.Single(outline.Roots).Children;
        Assert.Equal(["decimal", "string"], conversions.Select(n => n.Name));
        Assert.Equal(["Money", "Money"], conversions.Select(n => n.ParameterTypes));
    }

    [Fact]
    public void ASyntaxErrorFileExtractsWhatItCanAndDoesNotThrow()
    {
        var outline = fixture.Extractor.Extract(
            """
            namespace Acme;

            class Widget
            {
                void Good() { }

                void Broken( {{{ }
            }
            """,
            CodeLanguage.CSharp);

        Assert.NotNull(outline);
        Assert.Contains(outline.Flatten(), n => n.Name == "Good");
    }

    [Fact]
    public void AFileWithNoDeclarationsReturnsNull()
    {
        Assert.Null(fixture.Extractor.Extract("// just a comment\n", CodeLanguage.CSharp));
        Assert.Null(fixture.Extractor.Extract(string.Empty, CodeLanguage.CSharp));
    }

    [Fact]
    public void AFileOverTheCapReturnsNull()
    {
        var huge = new string('a', TreeSitterSymbolExtractor.MaxFileBytes + 1);
        Assert.Null(fixture.Extractor.Extract($"class W {{ string s = \"{huge}\"; }}", CodeLanguage.CSharp));
    }

    [Fact]
    public void AMultiByteFileOverTheByteCapReturnsNull()
    {
        var padding = new string('é', (TreeSitterSymbolExtractor.MaxFileBytes / 2) + 1);
        var source = $"class W {{ string s = \"{padding}\"; }}";

        Assert.True(source.Length <= TreeSitterSymbolExtractor.MaxFileBytes);
        Assert.True(Encoding.UTF8.GetByteCount(source) > TreeSitterSymbolExtractor.MaxFileBytes);
        Assert.Null(fixture.Extractor.Extract(source, CodeLanguage.CSharp));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void LineEndingsAreNormalizedBeforeEncoding(string newline)
    {
        var source = string.Join(newline, ["namespace Acme;", "", "class Widget", "{", "    void Resize() { }", "}"]);
        var resize = Find(fixture.Outline(source), "Resize");

        Assert.Equal(5, resize.StartLine);
        Assert.Equal(5, resize.EndLine);
    }

    [Fact]
    public void TheFixtureFileProducesTheCheckedInOutline()
    {
        Assert.Equal(
            CodeIntelSamples.ExpectedOutline,
            CodeIntelSamples.Render(fixture.Outline(CodeIntelSamples.Sample)));
    }

    [Fact]
    public void ExtractionIsSafeFromSeveralThreadsAtOnce()
    {
        using var extractor = new TreeSitterSymbolExtractor(poolCapacity: 2);
        var expected = CodeIntelSamples.Render(
            extractor.Extract(CodeIntelSamples.Sample, CodeLanguage.CSharp)
            ?? throw new InvalidOperationException("Expected an outline."));

        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            var outline = extractor.Extract(CodeIntelSamples.Sample, CodeLanguage.CSharp);
            Assert.NotNull(outline);
            Assert.Equal(expected, CodeIntelSamples.Render(outline));
        });
    }

    internal static OutlineNode Find(FileOutline outline, string name) =>
        outline.Flatten().First(n => n.Name == name);
}

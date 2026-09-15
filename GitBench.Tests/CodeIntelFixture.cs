using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

/// <summary>Compiled once for the whole collection: the grammar set builds a query per bundled
/// grammar, and paying that per test class costs seconds.</summary>
public sealed class CodeIntelFixture : IDisposable
{
    private readonly TreeSitterGrammars _grammars = new();
    private readonly TreeSitterSymbolExtractor _extractor;
    private readonly TreeSitterSyntaxHighlighter _highlighter;

    public CodeIntelFixture()
    {
        _extractor = new TreeSitterSymbolExtractor(_grammars);
        _highlighter = new TreeSitterSyntaxHighlighter(_grammars);
        Colors = new RoutedSyntaxHighlighter(_highlighter, new PlainText());
    }

    internal TreeSitterGrammars Grammars => _grammars;

    internal ISymbolExtractor Extractor => _extractor;

    /// <summary>The parser-backed extractor itself, for the tests that keep a tree between edits.</summary>
    internal TreeSitterSymbolExtractor Symbols => _extractor;

    internal TreeSitterSyntaxHighlighter Highlighter => _highlighter;

    /// <summary>The parser alone, with nothing to fall back to.</summary>
    internal ISyntaxHighlighter Colors { get; }

    internal FileOutline Outline(string csharp) => Outline(csharp, CodeLanguage.CSharp);

    internal FileOutline Outline(string source, CodeLanguage language) =>
        _extractor.Extract(source, language)
        ?? throw new InvalidOperationException("Expected an outline, got none.");

    public void Dispose() => _grammars.Dispose();
}

[CollectionDefinition(nameof(CodeIntelCollection))]
public sealed class CodeIntelCollection : ICollectionFixture<CodeIntelFixture>;

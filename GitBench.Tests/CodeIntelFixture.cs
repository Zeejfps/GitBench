using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

public sealed class CodeIntelFixture : IDisposable
{
    private readonly TreeSitterSymbolExtractor _extractor = new();
    private readonly Lazy<TreeSitterSyntaxHighlighter> _highlighter = new(() => new TreeSitterSyntaxHighlighter());

    internal ISymbolExtractor Extractor => _extractor;

    /// <summary>The parser-backed extractor itself, for the tests that keep a tree between edits.</summary>
    internal TreeSitterSymbolExtractor Symbols => _extractor;

    /// <summary>Compiled once for the whole collection: a highlighter builds a query per bundled
    /// grammar, and paying that per test class costs seconds.</summary>
    internal TreeSitterSyntaxHighlighter Highlighter => _highlighter.Value;

    internal FileOutline Outline(string csharp) => Outline(csharp, CodeLanguage.CSharp);

    internal FileOutline Outline(string source, CodeLanguage language) =>
        _extractor.Extract(source, language)
        ?? throw new InvalidOperationException("Expected an outline, got none.");

    public void Dispose()
    {
        if (_highlighter.IsValueCreated) _highlighter.Value.Dispose();
        _extractor.Dispose();
    }
}

[CollectionDefinition(nameof(CodeIntelCollection))]
public sealed class CodeIntelCollection : ICollectionFixture<CodeIntelFixture>;

using GitBench.Features.CodeIntel;
using Xunit;

namespace GitBench.Tests;

public sealed class CodeIntelFixture : IDisposable
{
    private readonly TreeSitterSymbolExtractor _extractor = new();

    internal ISymbolExtractor Extractor => _extractor;

    internal FileOutline Outline(string csharp) => Outline(csharp, CodeLanguage.CSharp);

    internal FileOutline Outline(string source, CodeLanguage language) =>
        _extractor.Extract(source, language)
        ?? throw new InvalidOperationException("Expected an outline, got none.");

    public void Dispose() => _extractor.Dispose();
}

[CollectionDefinition(nameof(CodeIntelCollection))]
public sealed class CodeIntelCollection : ICollectionFixture<CodeIntelFixture>;

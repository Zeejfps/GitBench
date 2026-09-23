using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// A workspace symbol search comes back in two shapes — the older SymbolInformation and the newer
// WorkspaceSymbol — which read the same here. A location with no range needs a resolve this client
// never advertises, so such an entry is left out rather than guessed at.
public sealed class WorkspaceSymbolShapeTests
{
    private static WorkspaceSymbols Read(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return WorkspaceSymbols.Reader.Read(document.RootElement);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NothingFoundIsAnEmptyList(string resultJson)
    {
        Assert.Empty(Read(resultJson).Items);
    }

    [Fact]
    public void SymbolInformationReadsWhole()
    {
        var symbol = Assert.Single(Read(
            """[{"name":"Login","kind":6,"containerName":"AuthService","location":{"uri":"file:///repo/src/Auth.cs","range":{"start":{"line":9,"character":16},"end":{"line":9,"character":21}}}}]""").Items);

        Assert.Equal("Login", symbol.Name);
        Assert.Equal(LspSymbolKind.Method, symbol.Kind);
        Assert.Equal("AuthService", symbol.ContainerName);
        Assert.Equal("file:///repo/src/Auth.cs", symbol.Location.Uri.Value);
        Assert.Equal(new LspLine(9), symbol.Location.Range.Start.Line);
        Assert.Equal(new LspCharacter(16), symbol.Location.Range.Start.Character);
    }

    [Fact]
    public void TheNewerShapeReadsTheSame_AndAnEmptyContainerIsNone()
    {
        var symbol = Assert.Single(Read(
            """[{"name":"Parser","kind":5,"containerName":"","location":{"uri":"file:///repo/p.rs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":6}}},"data":{"x":1}}]""").Items);

        Assert.Equal(LspSymbolKind.Class, symbol.Kind);
        Assert.Null(symbol.ContainerName);
    }

    [Fact]
    public void ALocationWithNoRange_IsLeftOut()
    {
        var symbols = Read(
            """[{"name":"Lazy","kind":12,"location":{"uri":"file:///repo/a.ts"}},{"name":"Eager","kind":12,"location":{"uri":"file:///repo/b.ts","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":5}}}}]""").Items;

        Assert.Equal("Eager", Assert.Single(symbols).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(255)]
    public void AKindTheProtocolDoesNotDefine_ReadsAsUnknown(int kind)
    {
        var symbol = Assert.Single(Read(
            $$$$$"""[{"name":"X","kind":{{{{{kind}}}}},"location":{"uri":"file:///repo/x.c","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}}]""").Items);

        Assert.Equal(LspSymbolKind.Unknown, symbol.Kind);
    }

    [Theory]
    [InlineData("""{"name":"X"}""")]
    [InlineData("""[{"name":"X","kind":"class","location":{"uri":"file:///x","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}}]""")]
    [InlineData("""[{"kind":5,"location":{"uri":"file:///x","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}}]""")]
    [InlineData("""[{"name":"X","kind":5}]""")]
    public void AShapeThatIsNeither_IsMalformed(string resultJson)
    {
        Assert.Throws<LspParseException>(() => Read(resultJson));
    }

    [Fact]
    public void TheRequestCarriesTheQuery()
    {
        var request = LspRequests.WorkspaceSymbol("FBVM");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) request.WriteParams(writer);

        using var sent = JsonDocument.Parse(stream.ToArray());
        Assert.Equal("workspace/symbol", request.Method.Name);
        Assert.Equal("FBVM", sent.RootElement.GetProperty("query").GetString());
    }
}

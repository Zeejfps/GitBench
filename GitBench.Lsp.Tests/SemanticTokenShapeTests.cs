using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// Semantic tokens arrive as one flat run of integers, five to a token, each position relative to the
// token before it, each type an index into a legend the server announced at startup. What these pin
// is the decoding: a start that is relative only on the same line is the mistake that shifts every
// color after the first line break.
public sealed class SemanticTokenShapeTests
{
    private static readonly SemanticTokensLegend Legend =
        new([new SemanticTokenType("class"), new SemanticTokenType("struct"), new SemanticTokenType("variable")]);

    private static SemanticTokens Read(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return SemanticTokens.ReaderFor(Legend).Read(document.RootElement);
    }

    private static ServerCapabilities Capabilities(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ServerCapabilities.Reader.Read(document.RootElement);
    }

    [Fact]
    public void PositionsAreRelativeToTheTokenBefore_AndAStartOnlyOnTheSameLine()
    {
        // line 1 col 4 "class"; same line +6 -> col 10 "struct"; two lines on, col 2 (not 12) "variable".
        var tokens = Read("""{"data":[1,4,3,0,0, 0,6,5,1,0, 2,2,1,2,0]}""").Tokens;

        Assert.Equal(
            [
                new SemanticToken(new LspLine(1), new LspCharacter(4), 3, new SemanticTokenType("class")),
                new SemanticToken(new LspLine(1), new LspCharacter(10), 5, new SemanticTokenType("struct")),
                new SemanticToken(new LspLine(3), new LspCharacter(2), 1, new SemanticTokenType("variable")),
            ],
            tokens);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"data":[]}""")]
    public void NothingClassifiedIsNoTokens(string resultJson)
    {
        Assert.Empty(Read(resultJson).Tokens);
    }

    [Theory]
    [InlineData("""{"data":[0,0,1,0]}""")]        // not a whole token
    [InlineData("""{"data":[0,0,1,9,0]}""")]      // a type the legend never named
    [InlineData("""{"data":[0,-1,1,0,0]}""")]     // a negative position
    [InlineData("""{"resultId":"1"}""")]          // no data at all
    public void APayloadThatCannotBeDecodedIsRefusedWhole(string resultJson)
    {
        Assert.Throws<LspParseException>(() => Read(resultJson));
    }

    [Fact]
    public void AServerThatClassifiesWholeDocumentsHandsOverItsLegend()
    {
        var capabilities = Capabilities(
            """{"capabilities":{"semanticTokensProvider":{"full":{"delta":true},"legend":{"tokenTypes":["struct","enum"],"tokenModifiers":[]}}}}""");

        var support = Assert.IsType<SemanticTokensSupport.WholeDocument>(capabilities.SemanticTokens);
        Assert.Equal([new SemanticTokenType("struct"), new SemanticTokenType("enum")], support.Legend.TokenTypes);
    }

    [Theory]
    [InlineData("""{"capabilities":{}}""")]
    [InlineData("""{"capabilities":{"semanticTokensProvider":{"range":true,"legend":{"tokenTypes":["struct"],"tokenModifiers":[]}}}}""")]
    [InlineData("""{"capabilities":{"semanticTokensProvider":{"full":false,"legend":{"tokenTypes":["struct"],"tokenModifiers":[]}}}}""")]
    public void AServerThatWillNotClassifyAWholeDocumentIsNotAsked(string json)
    {
        Assert.IsType<SemanticTokensSupport.None>(Capabilities(json).SemanticTokens);
    }

    [Fact]
    public void TheOpeningRequestOffersWholeDocumentTokensInTheRelativeFormat()
    {
        var request = LspHandshake.Initialize(DocumentUri.OfFile(Path.GetFullPath("repo")), processId: 1);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) request.WriteParams(writer);
        using var sent = JsonDocument.Parse(stream.ToArray());

        var semantic = sent.RootElement.GetProperty("capabilities").GetProperty("textDocument").GetProperty("semanticTokens");
        Assert.True(semantic.GetProperty("requests").GetProperty("full").GetBoolean());
        Assert.Equal(["relative"], semantic.GetProperty("formats").EnumerateArray().Select(f => f.GetString()));
        Assert.Contains("struct", semantic.GetProperty("tokenTypes").EnumerateArray().Select(t => t.GetString()));
    }
}

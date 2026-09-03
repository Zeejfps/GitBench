using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// Find-references comes back as one flat array of locations — no single-object form, no link form —
// and servers spell "none" as null and as an empty array interchangeably. What these pin beyond the
// collapse is the question asked: the count above a declaration is of its usages, so a request that
// includes the declaration reads as one usage everywhere, including on symbols nobody calls.
public sealed class ReferenceShapeTests
{
    private const string ASite =
        """{"uri":"file:///repo/src/lib.rs","range":{"start":{"line":10,"character":4},"end":{"line":10,"character":8}}}""";

    private const string AnotherSite =
        """{"uri":"file:///repo/src/other.rs","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":6}}}""";

    private static References Read(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return References.Reader.Read(document.RootElement);
    }

    private static IReadOnlyList<Documents.Location> SitesOf(string resultJson) =>
        Assert.IsType<References.Sites>(Read(resultJson)).Items;

    // A symbol nothing uses is the case the count exists to show, and both spellings of it have to
    // arrive as the same thing.
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NoUsagesIsACaseOfItsOwnRatherThanAnEmptyList(string resultJson)
    {
        Assert.IsType<References.None>(Read(resultJson));
    }

    [Fact]
    public void OneLocationReadsAsOneSite()
    {
        var site = Assert.Single(SitesOf($"[{ASite}]"));

        Assert.Equal("file:///repo/src/lib.rs", site.Uri.Value);
        Assert.Equal(new LspLine(10), site.Range.Start.Line);
        Assert.Equal(new LspCharacter(4), site.Range.Start.Character);
        Assert.Equal(new LspCharacter(8), site.Range.End.Character);
    }

    [Fact]
    public void AnArrayOfLocationsKeepsTheServersOrder()
    {
        var sites = SitesOf($"[{ASite},{AnotherSite}]");

        Assert.Equal(2, sites.Count);
        Assert.Equal("file:///repo/src/lib.rs", sites[0].Uri.Value);
        Assert.Equal("file:///repo/src/other.rs", sites[1].Uri.Value);
    }

    [Fact]
    public void APercentEncodedPathReadsBackAsThePathItEncodes()
    {
        var site = Assert.Single(SitesOf(
            """[{"uri":"file:///repo/a%20b/%C3%A9.rs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}]"""));

        Assert.Contains("a b", site.Uri.LocalPath);
        Assert.Contains("é.rs", site.Uri.LocalPath);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"file:///a.rs\"")]
    [InlineData(ASite)]
    [InlineData("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}]""")]
    [InlineData("""[{"uri":"file:///a.rs"}]""")]
    [InlineData("""[{"uri":"src/lib.rs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}]""")]
    [InlineData("""[{"uri":"file:///a.rs","range":{"start":{"line":-3,"character":0},"end":{"line":0,"character":1}}}]""")]
    [InlineData("""["file:///a.rs"]""")]
    public void AnAnswerThatCannotBeTrustedIsRefusedRatherThanGuessedAt(string resultJson)
    {
        Assert.Throws<LspParseException>(() => Read(resultJson));
    }

    // Definitions may arrive as the richer link form; references may not. Reading one anyway would
    // mean inventing a location out of a shape the protocol never promised here, and a wrong count
    // is worse than none.
    [Fact]
    public void TheLinkFormIsRefusedRatherThanReadAsALocation()
    {
        Assert.Throws<LspParseException>(() => Read(
            """[{"targetUri":"file:///repo/src/lib.rs","targetRange":{"start":{"line":1,"character":0},"end":{"line":2,"character":0}}}]"""));
    }

    [Fact]
    public void OneUnreadableSiteRefusesTheWholeAnswerRatherThanMiscountingTheRest()
    {
        Assert.Throws<LspParseException>(() => Read($$"""[{"uri":"file:///a.rs"},{{ASite}}]"""));
    }

    // The declaration is not a usage of itself. A server asked to include it answers one higher
    // everywhere, which is invisible until a symbol nobody calls reads as "1 usage".
    [Fact]
    public async Task TheRequestAsksForUsagesWithoutTheDeclaration()
    {
        await using var fx = new LspFixture();
        var sent = fx.AskReferences();

        var asked = await fx.Server.NextRequest();

        Assert.Equal("textDocument/references", asked.Method);
        Assert.Equal(
            LspFixture.SomeFile.Value,
            asked.Params.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(3, asked.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.False(asked.Params.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());

        await fx.Server.ReplyOk(asked.Id, "[]");
        await sent;
    }
}

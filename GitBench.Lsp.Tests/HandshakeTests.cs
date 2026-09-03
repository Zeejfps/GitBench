using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The opening exchange, and what this client refuses to talk to. A server that counts positions
/// differently is the case worth having: it answers every question successfully, with offsets that
/// address the wrong characters.
/// </summary>
public sealed class HandshakeTests
{
    private const string Minimal = """{"capabilities":{}}""";

    [Fact]
    public async Task TheOpeningRequestAsksForTheOnlyEncodingThisClientCanAddress()
    {
        await using var fx = new LspFixture();
        var sent = fx.Connection.Send(
            LspHandshake.Initialize(LspFixture.SomeFile, processId: 1234), LspFixture.Budget);

        var asked = await fx.Server.NextRequest();

        Assert.Equal("initialize", asked.Method);
        var encodings = asked.Params
            .GetProperty("capabilities").GetProperty("general").GetProperty("positionEncodings")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["utf-16"], encodings);

        await fx.Server.ReplyOk(asked.Id, Minimal);
        await sent;
    }

    [Fact]
    public async Task TheOpeningRequestNamesTheRootAndThisProcess()
    {
        await using var fx = new LspFixture();
        var sent = fx.Connection.Send(
            LspHandshake.Initialize(LspFixture.SomeFile, processId: 4321), LspFixture.Budget);

        var asked = await fx.Server.NextRequest();

        Assert.Equal(LspFixture.SomeFile.Value, asked.Params.GetProperty("rootUri").GetString());
        Assert.Equal(4321, asked.Params.GetProperty("processId").GetInt32());

        await fx.Server.ReplyOk(asked.Id, Minimal);
        await sent;
    }

    // A server that says nothing about encoding is not disagreeing; the protocol's default is the
    // one we asked for.
    [Fact]
    public void AServerThatNeverMentionsPositionEncodingCountsAsWeDo()
    {
        var capabilities = Read(Minimal);

        Assert.Equal("utf-16", capabilities.PositionEncoding);
        Assert.True(capabilities.CountsPositionsAsWeDo);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("UTF-16")]
    public void AServerCountingInUtf16CountsAsWeDo(string encoding) =>
        Assert.True(Read(Announcing(encoding)).CountsPositionsAsWeDo);

    // The refusal that matters. Every answer from such a server parses; the offsets in them address
    // different characters than the ones on screen, which is invisible until a line has non-ASCII in it.
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-32")]
    public void AServerCountingSomeOtherWayDoesNot(string encoding) =>
        Assert.False(Read(Announcing(encoding)).CountsPositionsAsWeDo);

    [Fact]
    public void AServerNamesItselfWhenItSaysSo()
    {
        Assert.Equal("rust-analyzer", Read("""{"capabilities":{},"serverInfo":{"name":"rust-analyzer"}}""").ServerName);
        Assert.Null(Read(Minimal).ServerName);
    }

    // Both forms mean yes: the protocol lets a server answer with true or with an options object,
    // and reading only the boolean silently loses every server that configures itself.
    [Theory]
    [InlineData("true")]
    [InlineData("""{"workDoneProgress":true}""")]
    public void ACapabilityAnnouncedEitherWayIsAvailable(string advertised)
    {
        var capabilities = Read(
            """{"capabilities":{"hoverProvider":""" + advertised +
            ""","definitionProvider":""" + advertised +
            ""","referencesProvider":""" + advertised + "}}");

        Assert.True(capabilities.SupportsHover);
        Assert.True(capabilities.SupportsDefinition);
        Assert.True(capabilities.SupportsReferences);
    }

    [Fact]
    public void ACapabilityLeftOutIsNotAvailable()
    {
        var capabilities = Read(Minimal);

        Assert.False(capabilities.SupportsHover);
        Assert.False(capabilities.SupportsDefinition);
        Assert.False(capabilities.SupportsReferences);
    }

    // One provider announced says nothing about the others: a server that answers definitions but
    // not references is ordinary, and asking it anyway spends a request on a MethodNotFound.
    [Fact]
    public void OneCapabilityDoesNotVouchForAnother()
    {
        var capabilities = Read("""{"capabilities":{"definitionProvider":true}}""");

        Assert.True(capabilities.SupportsDefinition);
        Assert.False(capabilities.SupportsReferences);
    }

    // The client says what it can take, and a server that registers capabilities dynamically reads
    // the block's presence rather than its contents.
    [Fact]
    public async Task TheOpeningRequestAnnouncesThatThisClientAsksForReferences()
    {
        await using var fx = new LspFixture();
        var sent = fx.Connection.Send(
            LspHandshake.Initialize(LspFixture.SomeFile, processId: 1234), LspFixture.Budget);

        var asked = await fx.Server.NextRequest();

        Assert.Equal(
            JsonValueKind.Object,
            asked.Params.GetProperty("capabilities").GetProperty("textDocument")
                .GetProperty("references").ValueKind);

        await fx.Server.ReplyOk(asked.Id, Minimal);
        await sent;
    }

    private static ServerCapabilities Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ServerCapabilities.Reader.Read(document.RootElement);
    }

    private static string Announcing(string encoding) =>
        """{"capabilities":{"positionEncoding":""" + Quote(encoding) + "}}";

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}

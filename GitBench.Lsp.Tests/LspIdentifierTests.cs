using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// The types that keep a request id, a document version and a line number from being the same int.
// These pin the two things a wrapper type has to earn its place: it is built by validating, and it
// survives a round trip through the wire unchanged.
public sealed class LspIdentifierTests
{
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_numeric_request_id_reads_back_as_the_same_number()
    {
        Assert.Equal(new RequestId.Number(42), RequestId.Read(Element("42")));
    }

    [Fact]
    public void A_string_request_id_reads_back_as_the_same_string()
    {
        Assert.Equal(new RequestId.Text("cfg-1"), RequestId.Read(Element("\"cfg-1\"")));
    }

    [Fact]
    public void A_numeric_id_and_the_same_digits_as_a_string_are_not_the_same_id()
    {
        Assert.NotEqual<RequestId>(new RequestId.Number(1), new RequestId.Text("1"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("1.5")]
    public void An_id_the_protocol_does_not_allow_is_refused(string json)
    {
        Assert.Throws<LspParseException>(() => RequestId.Read(Element(json)));
    }

    [Fact]
    public void A_document_uri_round_trips_through_a_path_with_spaces_and_accents()
    {
        var path = Path.Combine(Path.GetTempPath(), "a b", "é.rs");

        Assert.Equal(path, DocumentUri.OfFile(path).LocalPath);
    }

    [Theory]
    [InlineData("src/main.rs")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_uri_that_is_not_absolute_is_refused(string value)
    {
        Assert.Throws<LspParseException>(() => DocumentUri.Parse(value));
    }

    [Fact]
    public void A_uri_with_no_local_path_reports_none_rather_than_inventing_one()
    {
        Assert.Equal(string.Empty, DocumentUri.Parse("jdt://contents/java.base/String.class").LocalPath);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_line_number_cannot_be_built(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LspLine(value));
    }

    [Fact]
    public void A_negative_character_offset_cannot_be_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LspCharacter(-1));
    }

    [Fact]
    public void The_first_line_and_the_first_column_are_zero()
    {
        // LSP counts from zero at both ends; a client that assumes one is off by a line everywhere.
        var position = LspPosition.At(0, 0);

        Assert.Equal(0, position.Line.Value);
        Assert.Equal(0, position.Character.Value);
    }

    [Theory]
    [InlineData(-32801, true)]
    [InlineData(-32002, true)]
    [InlineData(-32802, true)]
    [InlineData(-32800, false)]
    [InlineData(-32603, false)]
    public void Only_the_codes_that_mean_ask_again_say_so(int code, bool retryable)
    {
        Assert.Equal(retryable, new LspErrorCode(code).MeansAskAgain);
    }
}

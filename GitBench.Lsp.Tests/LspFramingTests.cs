using System.Text;
using Xunit;

namespace GitBench.Lsp.Tests;

// The Content-Length layer, on its own: what counts as a frame, what counts as the end, and what
// counts as a lie. The rule these pin together is that the reader never invents a message — every
// byte it hands up was declared, and anything it cannot frame is named rather than guessed at.
public sealed class LspFramingTests
{
    private static LspFrameReader Reading(string wire, LspFrameLimits? limits = null) =>
        new(Bytes.Of(wire), limits);

    private static string PayloadOf(FrameRead read) =>
        Encoding.UTF8.GetString(Assert.IsType<FrameRead.Frame>(read).Payload);

    [Fact]
    public async Task A_framed_payload_is_delivered_exactly()
    {
        var reader = Reading(Bytes.Frame("""{"jsonrpc":"2.0"}"""));

        Assert.Equal("""{"jsonrpc":"2.0"}""", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task Frames_arriving_together_are_delivered_one_at_a_time_in_order()
    {
        var reader = Reading(Bytes.Frame("\"first\"") + Bytes.Frame("\"second\"") + Bytes.Frame("\"third\""));

        Assert.Equal("\"first\"", PayloadOf(await reader.ReadAsync()));
        Assert.Equal("\"second\"", PayloadOf(await reader.ReadAsync()));
        Assert.Equal("\"third\"", PayloadOf(await reader.ReadAsync()));
    }

    [Theory]
    [InlineData("Content-Length: 2\r\n\r\n")]
    [InlineData("content-length: 2\r\n\r\n")]
    [InlineData("CONTENT-LENGTH:2\r\n\r\n")]
    [InlineData("Content-Length:  2  \r\n\r\n")]
    [InlineData("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 2\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nX-Something: else\r\n\r\n")]
    public async Task A_header_block_is_read_however_the_server_spells_it(string header)
    {
        var reader = Reading(header + "{}");

        Assert.Equal("{}", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task A_declared_length_of_zero_is_an_empty_frame_not_an_end()
    {
        var reader = Reading("Content-Length: 0\r\n\r\n" + Bytes.Frame("{}"));

        Assert.Empty(Assert.IsType<FrameRead.Frame>(await reader.ReadAsync()).Payload);
        Assert.Equal("{}", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task A_payload_is_measured_in_bytes_not_characters()
    {
        // The classic framing bug: these characters are 2, 3 and 4 bytes each.
        const string text = """{"text":"héllo 世界 🚀"}""";
        var wire = new MemoryStream();
        var writer = new LspFrameWriter(wire);
        await writer.WriteAsync(Bytes.Utf8(text));

        var reader = new LspFrameReader(new MemoryStream(wire.ToArray()));
        Assert.Equal(text, PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task A_payload_far_larger_than_the_read_buffer_arrives_whole()
    {
        var big = new string('x', 1_000_000);
        var reader = Reading(Bytes.Frame($"\"{big}\""));

        Assert.Equal($"\"{big}\"", PayloadOf(await reader.ReadAsync()));
    }

    // ---- ends: three of them, and they are not the same event ----

    [Fact]
    public async Task A_stream_that_ends_between_messages_is_a_clean_close()
    {
        var reader = Reading(Bytes.Frame("{}"));

        Assert.IsType<FrameRead.Frame>(await reader.ReadAsync());
        Assert.IsType<FrameRead.Closed>(await reader.ReadAsync());
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_payload_is_truncation()
    {
        var reader = Reading("Content-Length: 40\r\n\r\n{\"half\":");

        Assert.IsType<FrameRead.Truncated>(await reader.ReadAsync());
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_header_block_is_truncation()
    {
        var reader = Reading("Content-Length: 4\r\n");

        Assert.IsType<FrameRead.Truncated>(await reader.ReadAsync());
    }

    // ---- lies and noise ----

    [Fact]
    public async Task Chatter_before_a_header_is_skipped_and_counted()
    {
        var reader = Reading("warning: something went wrong\r\n" + Bytes.Frame("{}"));

        var frame = Assert.IsType<FrameRead.Frame>(await reader.ReadAsync());
        Assert.Equal("{}", Encoding.UTF8.GetString(frame.Payload));
        Assert.True(frame.SkippedBytes > 0, "the discarded chatter should be reported, not hidden");
    }

    [Fact]
    public async Task A_declared_length_is_authoritative_even_when_it_is_short()
    {
        // The reader cannot know the payload was meant to be longer; what it must not do is deliver
        // more than was declared and let the extra bytes pass as part of the message.
        var reader = Reading("Content-Length: 5\r\n\r\n" + """{"a":1}""");

        Assert.Equal("""{"a":""", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task A_lying_length_costs_one_message_and_not_the_connection()
    {
        var reader = Reading(
            Bytes.Frame("""{"good":1}""") +
            "Content-Length: 5\r\n\r\n" + """{"a":1}""" + "\r\n" +
            Bytes.Frame("""{"after":2}"""));

        Assert.Equal("""{"good":1}""", PayloadOf(await reader.ReadAsync()));
        Assert.Equal("""{"a":""", PayloadOf(await reader.ReadAsync()));
        Assert.Equal("""{"after":2}""", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task A_length_longer_than_the_payload_ends_as_truncation()
    {
        var reader = Reading("Content-Length: 4000\r\n\r\n" + """{"a":1}""");

        Assert.IsType<FrameRead.Truncated>(await reader.ReadAsync());
    }

    [Fact]
    public async Task A_header_block_that_never_terminates_stops_at_its_limit()
    {
        var reader = new LspFrameReader(new EndlessStream((byte)'x'), new LspFrameLimits(MaxHeaderBytes: 4096));

        var read = Assert.IsType<FrameRead.Malformed>(await reader.ReadAsync());
        Assert.Equal(FrameFault.HeaderTooLong, read.Fault);
    }

    [Fact]
    public async Task A_declared_length_beyond_the_limit_is_refused_before_the_body_is_read()
    {
        // The body is not on the wire at all: reaching a verdict at all proves the count was checked
        // before anything was buffered for it.
        var reader = new LspFrameReader(Bytes.Of("Content-Length: 999999999999\r\n\r\n"), new LspFrameLimits(MaxPayloadBytes: 1024));

        var read = Assert.IsType<FrameRead.Malformed>(await reader.ReadAsync());
        Assert.Equal(FrameFault.PayloadTooLarge, read.Fault);
    }

    [Fact]
    public async Task A_line_feed_without_a_carriage_return_is_refused()
    {
        var reader = Reading("Content-Length: 2\n\n{}");

        var read = Assert.IsType<FrameRead.Malformed>(await reader.ReadAsync());
        Assert.Equal(FrameFault.BareLineFeed, read.Fault);
    }

    [Fact]
    public async Task A_header_block_without_a_content_length_is_refused()
    {
        var reader = Reading("Content-Type: application/json\r\n\r\n{}");

        var read = Assert.IsType<FrameRead.Malformed>(await reader.ReadAsync());
        Assert.Equal(FrameFault.MissingContentLength, read.Fault);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("")]
    public async Task A_content_length_that_is_not_a_count_is_refused(string value)
    {
        var reader = Reading($"Content-Length: {value}\r\n\r\n{{}}");

        var read = Assert.IsType<FrameRead.Malformed>(await reader.ReadAsync());
        Assert.Equal(FrameFault.UnreadableContentLength, read.Fault);
    }

    // ---- writing ----

    [Fact]
    public async Task What_the_writer_produces_is_what_the_reader_reads_back()
    {
        var wire = new MemoryStream();
        var writer = new LspFrameWriter(wire);
        await writer.WriteAsync(Bytes.Utf8("""{"one":1}"""));
        await writer.WriteAsync(Bytes.Utf8("""{"two":2}"""));

        var reader = new LspFrameReader(new MemoryStream(wire.ToArray()));
        Assert.Equal("""{"one":1}""", PayloadOf(await reader.ReadAsync()));
        Assert.Equal("""{"two":2}""", PayloadOf(await reader.ReadAsync()));
    }

    [Fact]
    public async Task Frames_written_at_the_same_time_do_not_interleave()
    {
        var wire = new ChunkRecordingStream(parkUntilReleased: true);
        var writer = new LspFrameWriter(wire);
        var first = $"\"{new string('a', 4000)}\"";
        var second = $"\"{new string('b', 4000)}\"";

        // The first frame is left half written on the wire when the second one is handed over.
        var a = writer.WriteAsync(Bytes.Utf8(first)).AsTask();
        await wire.Arrived(1);
        var b = writer.WriteAsync(Bytes.Utf8(second)).AsTask();
        wire.Release();
        await Task.WhenAll(a, b);

        // Two whole frames come back out, in whichever order they took their turn.
        var reader = new LspFrameReader(new MemoryStream(wire.All));
        var payloads = new[] { PayloadOf(await reader.ReadAsync()), PayloadOf(await reader.ReadAsync()) };
        Assert.Contains(first, payloads);
        Assert.Contains(second, payloads);
    }
}

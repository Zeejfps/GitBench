using System.Globalization;
using System.Text;

namespace GitBench.Lsp;

/// <summary>What a frame reader can produce. Ending cleanly, ending mid-message and being lied to are
/// three different events with three different responses, so they are three cases.</summary>
public abstract record FrameRead
{
    private FrameRead() { }

    /// <summary>One complete payload. <paramref name="SkippedBytes"/> counts anything that had to be
    /// discarded to find its header — a server logging to stdout, or the tail of a lying frame.</summary>
    public sealed record Frame(byte[] Payload, int SkippedBytes = 0) : FrameRead;

    /// <summary>The stream ended between messages. Normal shutdown.</summary>
    public sealed record Closed : FrameRead;

    /// <summary>The stream ended part way through a header block or a payload.</summary>
    public sealed record Truncated(string Detail) : FrameRead;

    /// <summary>The bytes cannot be framed. The connection cannot be trusted to resynchronise.</summary>
    public sealed record Malformed(FrameFault Fault, string Detail) : FrameRead;
}

public enum FrameFault
{
    /// <summary>A header block ended without a Content-Length.</summary>
    MissingContentLength,

    /// <summary>Content-Length was present but not a non-negative integer.</summary>
    UnreadableContentLength,

    /// <summary>A header block ran past its size limit without terminating.</summary>
    HeaderTooLong,

    /// <summary>A line ended with a bare LF. The protocol says CRLF, and guessing here desynchronises.</summary>
    BareLineFeed,

    /// <summary>A payload larger than this client will buffer.</summary>
    PayloadTooLarge,
}

public sealed record LspFrameLimits(int MaxHeaderBytes = 8 * 1024, int MaxPayloadBytes = 32 * 1024 * 1024)
{
    public static readonly LspFrameLimits Default = new();
}

/// <summary>
/// Reads Content-Length framed payloads off a byte stream. Lenient about headers it does not know and
/// about noise between messages; strict about line endings and about the byte count, because a
/// framing guess is indistinguishable from a working connection until it is far too late.
/// </summary>
public sealed class LspFrameReader(Stream stream, LspFrameLimits? limits = null)
{
    private const string ContentLength = "content-length";
    private const string ContentType = "content-type";

    private readonly LspFrameLimits _limits = limits ?? LspFrameLimits.Default;
    private byte[] _buffer = new byte[8 * 1024];
    private int _start;
    private int _end;
    private bool _eof;

    public async ValueTask<FrameRead> ReadAsync(CancellationToken ct = default)
    {
        long? length = null;
        var sawAnyHeader = false;
        var headerBytes = 0;
        var skipped = 0;

        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            switch (line)
            {
                case LineOutcome.EndOfStream end:
                    return headerBytes == 0 && end.Pending == 0
                        ? new FrameRead.Closed()
                        : new FrameRead.Truncated("the stream ended inside a header block");

                case LineOutcome.BareLf:
                    return new FrameRead.Malformed(FrameFault.BareLineFeed, "a header line ended with LF instead of CRLF");

                case LineOutcome.TooLong:
                    return new FrameRead.Malformed(FrameFault.HeaderTooLong, $"no header terminator within {_limits.MaxHeaderBytes} bytes");

                case LineOutcome.Line(var text, var consumed):
                    headerBytes += consumed;
                    if (headerBytes > _limits.MaxHeaderBytes)
                        return new FrameRead.Malformed(FrameFault.HeaderTooLong, $"header block exceeded {_limits.MaxHeaderBytes} bytes");

                    if (text.Length == 0)
                    {
                        if (length is { } declared)
                            return await ReadBodyAsync(declared, skipped, ct).ConfigureAwait(false);
                        if (sawAnyHeader)
                            return new FrameRead.Malformed(FrameFault.MissingContentLength, "a header block carried no Content-Length");
                        skipped += consumed;
                        continue;
                    }

                    // The protocol defines two header fields. Anything else on this stream is a server
                    // logging where it speaks, and is discarded rather than being taken for a header.
                    var colon = text.IndexOf(':');
                    var name = colon > 0 ? text.AsSpan(0, colon).Trim() : [];
                    if (!name.Equals(ContentLength, StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(ContentType, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped += consumed;
                        continue;
                    }

                    sawAnyHeader = true;
                    if (!name.Equals(ContentLength, StringComparison.OrdinalIgnoreCase)) continue;

                    var value = text.AsSpan(colon + 1).Trim();
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                        return new FrameRead.Malformed(FrameFault.UnreadableContentLength, $"Content-Length was '{value}'");
                    if (parsed > _limits.MaxPayloadBytes)
                        return new FrameRead.Malformed(FrameFault.PayloadTooLarge, $"a {parsed} byte payload exceeds the {_limits.MaxPayloadBytes} byte limit");
                    length = parsed;
                    continue;

                default:
                    throw new NotSupportedException($"unhandled line outcome {line.GetType().Name}");
            }
        }
    }

    private async ValueTask<FrameRead> ReadBodyAsync(long length, int skipped, CancellationToken ct)
    {
        while (_end - _start < length)
        {
            if (!await FillAsync(ct).ConfigureAwait(false))
                return new FrameRead.Truncated(
                    $"the stream ended {length - (_end - _start)} bytes short of a {length} byte payload");
        }

        var payload = new byte[length];
        Buffer.BlockCopy(_buffer, _start, payload, 0, (int)length);
        _start += (int)length;
        return new FrameRead.Frame(payload, skipped);
    }

    private abstract record LineOutcome
    {
        public sealed record Line(string Text, int Consumed) : LineOutcome;

        public sealed record EndOfStream(int Pending) : LineOutcome;

        public sealed record BareLf : LineOutcome;

        public sealed record TooLong : LineOutcome;
    }

    private async ValueTask<LineOutcome> ReadLineAsync(CancellationToken ct)
    {
        var scanned = 0;
        while (true)
        {
            for (var i = _start + scanned; i < _end; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;
                if (i == _start || _buffer[i - 1] != (byte)'\r') return new LineOutcome.BareLf();

                var text = Encoding.ASCII.GetString(_buffer, _start, i - 1 - _start);
                var consumed = i + 1 - _start;
                _start = i + 1;
                return new LineOutcome.Line(text, consumed);
            }

            scanned = _end - _start;
            if (scanned > _limits.MaxHeaderBytes) return new LineOutcome.TooLong();
            if (!await FillAsync(ct).ConfigureAwait(false)) return new LineOutcome.EndOfStream(scanned);
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_eof) return false;

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

        int read;
        try
        {
            read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A pipe whose far end went away reads as an end of stream, not as a crash.
            read = 0;
        }
        catch (ObjectDisposedException)
        {
            read = 0;
        }

        if (read <= 0)
        {
            _eof = true;
            return false;
        }

        _end += read;
        return true;
    }
}

/// <summary>Writes Content-Length framed payloads. Frames from concurrent callers never interleave.</summary>
public sealed class LspFrameWriter(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"Content-Length: {payload.Length}\r\n\r\n"));

        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();
}

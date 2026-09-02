using System.Threading.Channels;

namespace GitBench.Lsp.Tests;

/// <summary>
/// One direction of an in-memory pipe. Writes are chunks a reader sees in order; closing it is an end
/// of stream, and writing into a closed one fails the way a dead pipe does.
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private ReadOnlyMemory<byte> _rest;
    private volatile bool _closed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void CloseWrite()
    {
        _closed = true;
        _chunks.Writer.TryComplete();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_rest.IsEmpty)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            if (_chunks.Reader.TryRead(out var chunk)) _rest = chunk;
        }

        var taken = Math.Min(buffer.Length, _rest.Length);
        _rest[..taken].CopyTo(buffer);
        _rest = _rest[taken..];
        return taken;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_closed || !_chunks.Writer.TryWrite(buffer.ToArray()))
            throw new IOException("the pipe is closed");
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        CloseWrite();
        base.Dispose(disposing);
    }
}

/// <summary>A stream over bytes already in hand, which ends when they run out.</summary>
internal static class Bytes
{
    public static Stream Of(params string[] parts) => new MemoryStream(Utf8(string.Concat(parts)));

    public static Stream Of(byte[] raw) => new MemoryStream(raw);

    public static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public static string Frame(string payload) =>
        $"Content-Length: {Utf8(payload).Length}\r\n\r\n{payload}";
}

/// <summary>A stream that never ends and never repeats a terminator. For proving a limit terminates.</summary>
internal sealed class EndlessStream(byte fill) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        buffer.Span.Fill(fill);
        return ValueTask.FromResult(buffer.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        buffer.AsSpan(offset, count).Fill(fill);
        return count;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Records every write the moment it arrives and then holds it open until the test lets go. Because
/// the recording happens before the park, a writer that does not take a turn is caught the first time
/// rather than on an unlucky schedule: its second frame's bytes are already on the wire while the
/// first frame is still half written.
/// </summary>
internal sealed class ChunkRecordingStream : Stream
{
    private readonly List<byte[]> _chunks = [];
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, TaskCompletionSource> _arrivals = [];
    private readonly bool _park;

    public ChunkRecordingStream(bool parkUntilReleased = false)
    {
        _park = parkUntilReleased;
        if (!parkUntilReleased) _open.TrySetResult();
    }

    /// <summary>Completes once the stream has been handed <paramref name="count"/> writes.</summary>
    public Task Arrived(int count)
    {
        lock (_chunks)
        {
            if (_chunks.Count >= count) return Task.CompletedTask;
            if (!_arrivals.TryGetValue(count, out var waiter))
                _arrivals[count] = waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return waiter.Task;
        }
    }

    /// <summary>Lets every parked write, and every later one, through.</summary>
    public void Release() => _open.TrySetResult();

    public IReadOnlyList<byte[]> Chunks
    {
        get { lock (_chunks) return _chunks.ToArray(); }
    }

    public byte[] All
    {
        get
        {
            lock (_chunks) return _chunks.SelectMany(c => c).ToArray();
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Record(buffer.ToArray());
        return _park ? new ValueTask(_open.Task) : ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, count).ToArray());

    private void Record(byte[] chunk)
    {
        TaskCompletionSource? arrived = null;
        lock (_chunks)
        {
            _chunks.Add(chunk);
            if (_arrivals.Remove(_chunks.Count, out var waiter)) arrived = waiter;
        }

        arrived?.TrySetResult();
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

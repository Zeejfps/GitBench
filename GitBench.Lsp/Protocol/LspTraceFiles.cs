using System.Globalization;
using System.Text;

namespace GitBench.Lsp;

/// <summary>
/// Traces written into one folder, a file per server process.
/// </summary>
/// <remarks>
/// Per process rather than one file for the run: servers talk over the top of each other, and the
/// question a trace is opened to answer is almost always about one of them. The folder is what ties
/// a run together, and the names sort into the order the servers started.
/// </remarks>
public sealed class LspTraceFolder(string directory) : ILspTraceSource
{
    private int _opened;

    public string Directory { get; } = directory;

    public ILspTrace Open(string server)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var order = Interlocked.Increment(ref _opened);
        var name = $"{stamp}-{order:00}-{Safe(server)}.log";
        return LspTraceFile.Create(Path.Combine(Directory, name));
    }

    private static string Safe(string server)
    {
        var safe = new StringBuilder(server.Length);
        foreach (var character in server)
            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        return safe.Length == 0 ? "server" : safe.ToString();
    }
}

/// <summary>
/// One server's conversation, appended to a file as it happens.
/// </summary>
/// <remarks>
/// <para>
/// Flushed on every line. A trace exists for the failures that end with the app or the server dying,
/// and a buffered one loses exactly the last few lines that say why.
/// </para>
/// <para>
/// Bodies are capped. A <c>didOpen</c> carries a whole file and a completion list can run to
/// megabytes; the heading above each body already carries the fields worth searching, so the tail of
/// a large message costs more to keep than it is worth. The full byte count is kept so a truncated
/// body is never mistaken for a small one.
/// </para>
/// </remarks>
public sealed class LspTraceFile : ILspTrace
{
    private const int BodyCap = 4000;

    private readonly TextWriter _writer;
    private readonly object _gate = new();

    private bool _closed;

    private LspTraceFile(TextWriter writer) => _writer = writer;

    /// <summary>
    /// Opens a trace, or falls back to recording nothing. A trace is a diagnostic aid, and a folder
    /// that cannot be written to is not a reason to fail the launch of the server being traced.
    /// </summary>
    public static ILspTrace Create(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new LspTraceFile(new StreamWriter(path, append: true) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return NoLspTrace.Instance;
        }
    }

    public void Message(LspTraffic direction, ReadOnlyMemory<byte> payload)
    {
        var heading = LspTraceHeading.Of(direction, payload);
        var text = Encoding.UTF8.GetString(payload.Span);
        var body = text.Length > BodyCap
            ? $"{text[..BodyCap]}… [{payload.Length} bytes]"
            : text;

        Write(heading, body);
    }

    public void Note(string text) => Write($"### {text}", body: null);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            try { _writer.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private void Write(string heading, string? body)
    {
        var at = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            if (_closed) return;
            try
            {
                _writer.WriteLine($"{at} {heading}");
                if (body is not null) _writer.WriteLine($"    {body}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _closed = true;
            }
        }
    }
}

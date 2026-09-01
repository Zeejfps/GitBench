using System.Diagnostics;
using System.Text;

namespace GitBench.Git;

/// <summary>
/// Reads blobs through one long-lived <c>git cat-file --batch</c> per repository instead of
/// spawning <c>git show</c> for every read.
/// </summary>
/// <remarks>
/// <para>
/// Reading a blob is a packfile lookup and an inflate — a fraction of a millisecond. Spawning a
/// process to do it costs fork/exec, dynamic linking, repository discovery and config parsing, so
/// the overhead was two orders of magnitude larger than the work: measured on this checkout,
/// 25 ms per <c>git show</c> against 0.2 ms per batch read. <c>--batch</c> pays the startup once
/// per repository and answers every later read over a pipe. It is plumbing, so the protocol is
/// stable, and every body is length-prefixed, so binary blobs survive it unaltered.
/// </para>
/// <para>
/// A reader starts on the first blob read for a repository and never before, so a sidebar of
/// thirty repositories costs nothing until one is actually read from, and only
/// <see cref="MaxLiveRepos"/> stay alive after that — the least recently used is shut down.
/// </para>
/// <para>
/// Nothing here can make a read wrong, only faster: every failure path returns
/// <see cref="Status.Unavailable"/>, which the caller answers by spawning <c>git show</c> exactly
/// as it did before.
/// </para>
/// </remarks>
internal sealed class GitBlobReader : IDisposable
{
    /// <summary>How many repositories keep a live reader. Working across a handful in one session
    /// is normal; having thirty registered and a process for each is not.</summary>
    private const int MaxLiveRepos = 6;

    /// <summary>A body we asked for but do not want — one past the caller's cap — still has to
    /// come out of the pipe before the next request can go in. Past this, ending the process and
    /// starting a fresh one on the next read is cheaper than draining what we will discard.</summary>
    private const long MaxDrainBytes = 32L * 1024 * 1024;

    /// <summary>Guards against a malformed stream: no <c>cat-file</c> header is near this long.</summary>
    private const int MaxHeaderBytes = 1024;

    public enum Status
    {
        /// <summary>Read. The out parameter holds the blob's bytes.</summary>
        Found,
        /// <summary>Git answered and there is nothing to return: no such object, not a blob, or
        /// past the caller's cap. The same answer <c>git show</c> would give, so do not retry.</summary>
        Missing,
        /// <summary>No usable reader. The caller must fall back to spawning git.</summary>
        Unavailable,
    }

    private readonly Func<string, ProcessStartInfo> _startInfo;
    private readonly object _gate = new();
    // Most recently used first. A handful of entries, so a linear scan beats a second index.
    private readonly LinkedList<Session> _live = new();
    private bool _disposed;

    /// <param name="startInfo">Builds the start info for <c>cat-file --batch</c> in a given working
    /// directory. Injected so the reader inherits the same git executable, PATH and environment as
    /// every other invocation rather than assembling its own.</param>
    public GitBlobReader(Func<string, ProcessStartInfo> startInfo) => _startInfo = startInfo;

    /// <summary>How many repositories currently hold a live reader. Never above
    /// <see cref="MaxLiveRepos"/>.</summary>
    public int LiveRepoCount { get { lock (_gate) return _live.Count; } }

    public Status TryRead(string workingDir, string revPath, long maxBytes, out byte[]? content)
    {
        content = null;
        // A newline in the request would desync a line-delimited protocol, and git does allow one
        // in a path. Rare enough to hand to the fallback rather than complicate the reader.
        if (revPath.AsSpan().IndexOfAny('\n', '\r') >= 0) return Status.Unavailable;

        var session = Acquire(workingDir);
        if (session == null) return Status.Unavailable;

        var status = session.Read(revPath, maxBytes, out content);
        // Covers both a broken pipe and a body we killed the process rather than drain.
        if (!session.Alive) Retire(session);
        return status;
    }

    private Session? Acquire(string workingDir)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            for (var node = _live.First; node != null; node = node.Next)
            {
                if (!string.Equals(node.Value.WorkingDir, workingDir, StringComparison.Ordinal)) continue;
                if (node.Value.Alive)
                {
                    _live.Remove(node);
                    _live.AddFirst(node);
                    return node.Value;
                }
                _live.Remove(node);
                node.Value.Dispose();
                break;
            }

            var started = Session.Start(workingDir, _startInfo(workingDir));
            if (started == null) return null;

            _live.AddFirst(started);
            while (_live.Count > MaxLiveRepos)
            {
                var evicted = _live.Last!;
                _live.Remove(evicted);
                evicted.Value.Dispose();
            }
            return started;
        }
    }

    private void Retire(Session session)
    {
        lock (_gate)
        {
            for (var node = _live.First; node != null; node = node.Next)
            {
                if (!ReferenceEquals(node.Value, session)) continue;
                _live.Remove(node);
                break;
            }
        }
        session.Dispose();
    }

    public void Dispose()
    {
        List<Session> live;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            live = [.. _live];
            _live.Clear();
        }
        foreach (var session in live) session.Dispose();
    }

    /// <summary>
    /// One repository's reader. Requests are serialized on its pipe: a read is a fraction of a
    /// millisecond, so a lock costs far less than a second process would, and it keeps the live
    /// process count equal to the live repository count.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public string WorkingDir { get; }

        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly object _pipe = new();
        private volatile bool _broken;

        private Session(string workingDir, Process process)
        {
            WorkingDir = workingDir;
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _stdout = process.StandardOutput.BaseStream;
        }

        public static Session? Start(string workingDir, ProcessStartInfo startInfo)
        {
            try
            {
                var process = Process.Start(startInfo);
                return process == null ? null : new Session(workingDir, process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException
                or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                return null;
            }
        }

        public bool Alive
        {
            get
            {
                if (_broken) return false;
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public Status Read(string revPath, long maxBytes, out byte[]? content)
        {
            content = null;
            lock (_pipe)
            {
                if (!Alive) return Status.Unavailable;
                try
                {
                    Request(revPath);
                    var header = ReadHeaderLine();

                    // A found blob answers "<oid> blob <size>"; everything else — missing,
                    // ambiguous, dangling — is a single line with no body to consume.
                    var parts = header.Split(' ');
                    if (parts.Length != 3 || !long.TryParse(parts[2], out var size) || size < 0)
                        return Status.Missing;

                    if (parts[1] != "blob" || size > maxBytes || size > Array.MaxLength)
                    {
                        // The body is already on its way regardless of whether we want it.
                        if (size > MaxDrainBytes)
                        {
                            _broken = true;
                            return Status.Missing;
                        }
                        Drain(size);
                        return Status.Missing;
                    }

                    content = ReadBody(size);
                    return Status.Found;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException
                    or InvalidOperationException or NotSupportedException)
                {
                    _broken = true;
                    return Status.Unavailable;
                }
            }
        }

        private void Request(string revPath)
        {
            var bytes = Encoding.UTF8.GetBytes(revPath);
            _stdin.Write(bytes, 0, bytes.Length);
            _stdin.WriteByte((byte)'\n');
            _stdin.Flush();
        }

        private string ReadHeaderLine()
        {
            var line = new List<byte>(64);
            while (true)
            {
                var b = _stdout.ReadByte();
                if (b < 0) throw new IOException("cat-file closed its output before answering.");
                if (b == '\n') return Encoding.UTF8.GetString([.. line]);
                if (line.Count >= MaxHeaderBytes) throw new IOException("cat-file header ran too long.");
                line.Add((byte)b);
            }
        }

        // Each body is followed by a newline that is not part of the blob.
        private byte[] ReadBody(long size)
        {
            var buffer = new byte[size];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = _stdout.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new IOException("cat-file output ended mid-blob.");
                offset += read;
            }
            if (_stdout.ReadByte() < 0) throw new IOException("cat-file output ended mid-blob.");
            return buffer;
        }

        private void Drain(long size)
        {
            var scratch = new byte[64 * 1024];
            var remaining = size + 1; // the body plus its trailing newline
            while (remaining > 0)
            {
                var read = _stdout.Read(scratch, 0, (int)Math.Min(scratch.Length, remaining));
                if (read <= 0) throw new IOException("cat-file output ended mid-blob.");
                remaining -= read;
            }
        }

        public void Dispose()
        {
            _broken = true;
            // Closing stdin is the documented way out: cat-file reads to EOF and exits. Killing is
            // for the case where it does not, so a shutdown can never wait on a wedged pipe.
            try { _stdin.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try
            {
                if (!_process.WaitForExit(500)) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
            {
            }
            _process.Dispose();
        }
    }
}

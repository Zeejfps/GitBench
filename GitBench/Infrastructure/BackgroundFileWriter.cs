namespace GitBench.Infrastructure;

// Writes a file off the UI thread, coalescing bursts. Callers serialize their state on the calling
// thread (cheap, and the only point where the live model is read) and hand the finished text here;
// the actual disk write — temp-file write plus atomic rename, the part with unpredictable latency —
// runs on a single background worker. Rapid Schedule calls collapse to the latest text, so a flurry
// of saves costs one write. Dispose drains the last pending write so nothing is lost on shutdown.
internal sealed class BackgroundFileWriter : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private string? _pending;
    private bool _draining;
    private bool _disposed;
    private Task _worker = Task.CompletedTask;

    public BackgroundFileWriter(string path) => _path = path;

    public void Schedule(string contents)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = contents;
            if (_draining) return;
            _draining = true;
            _worker = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            string contents;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _draining = false;
                    return;
                }
                contents = _pending;
                _pending = null;
            }

            try { AtomicFile.WriteAllText(_path, contents); }
            catch (Exception ex) { Console.WriteLine($"[BackgroundFileWriter] write to {_path} failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            _disposed = true;
            worker = _worker;
        }

        try { worker.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* fall through to the final synchronous flush below */ }

        string? contents;
        lock (_gate)
        {
            contents = _pending;
            _pending = null;
        }
        if (contents is not null)
        {
            try { AtomicFile.WriteAllText(_path, contents); }
            catch (Exception ex) { Console.WriteLine($"[BackgroundFileWriter] final flush to {_path} failed: {ex.Message}"); }
        }
    }
}

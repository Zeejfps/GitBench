namespace GitBench.App;

// Last-chance crash reporting. Release builds are NativeAOT: an unhandled exception on any
// thread aborts the process with nothing on disk, so occasional user crashes are undiagnosable.
// These handlers append the exception to crash.log in the app-data directory before the process
// dies; native crashes (segfaults, X errors) never reach managed handlers and still need stderr.
internal static class CrashLog
{
    private const long MaxLogBytes = 1024 * 1024;

    public static void Install(string path)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(path, "AppDomain.UnhandledException", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(path, "TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static void Write(string path, string source, object? exception)
    {
        var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source} " +
                    $"({Environment.OSVersion}, thread {Environment.CurrentManagedThreadId}){Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            Console.Error.Write(entry);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // A runaway repeating exception must not grow the log unbounded; start over past the cap.
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                File.Delete(path);
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Never let crash reporting itself throw — the original exception must win.
        }
    }
}

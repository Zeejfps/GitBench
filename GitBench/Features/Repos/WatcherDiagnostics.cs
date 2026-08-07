using System.Collections.Concurrent;

namespace GitBench.Features.Repos;

// Filesystem watching is a private resource on Windows and macOS, and a shared, per-user,
// whole-system one on Linux: a recursive FileSystemWatcher there is one inotify instance plus one
// watch per directory in the subtree, drawn from fs.inotify.max_user_instances (128 by default)
// and fs.inotify.max_user_watches. Every process the user is running draws from the same two
// pools, so a GitBench with enough repos open can stop editors, file managers and IDEs from
// watching anything at all — the app's own failure is the smaller half of the damage.
//
// Swallowing the resulting IOException made that doubly invisible: GitBench went quietly blind to
// external changes, and the user was left diagnosing why unrelated applications had broken. This
// reports the failure, and the approach to the instance limit, to stderr — the one channel that
// survives a NativeAOT release build.
internal static class WatcherDiagnostics
{
    private const double WarnFraction = 0.5;

    private static readonly Lazy<int?> MaxUserInstances = new(() => ReadProcLimit("max_user_instances"));
    private static readonly Lazy<int?> MaxUserWatches = new(() => ReadProcLimit("max_user_watches"));

    // Distinct causes each get one line; a repeating cause does not. A disconnected drive and an
    // exhausted budget are different problems and the user needs to see both.
    private static readonly ConcurrentDictionary<string, byte> ReportedFailures = new();

    private static int _live;
    private static int _budgetWarned;

    public static int Live => Volatile.Read(ref _live);

    public static void Created(string repoPath)
    {
        var live = Interlocked.Increment(ref _live);
        if (MaxUserInstances.Value is not { } limit) return;
        if (live < limit * WarnFraction) return;
        if (Interlocked.Exchange(ref _budgetWarned, 1) != 0) return;
        Report($"watching {live} repositories holds {live} of this user's {limit} inotify instances, " +
               "a budget shared with every other application. Close repositories, or raise it with " +
               "`sudo sysctl fs.inotify.max_user_instances=<n>`.");
    }

    public static void Disposed() => Interlocked.Decrement(ref _live);

    public static void Failed(string repoPath, Exception e)
    {
        var cause = $"{e.GetType().Name}: {e.Message}";
        if (!ReportedFailures.TryAdd(cause, 0)) return;

        // The .NET message already names whichever inotify limit was hit and its value; the /proc
        // reads are here so the *other* limit and our own share of it are in the same line.
        var limits = OperatingSystem.IsLinux()
            ? $" Linux inotify limits: max_user_instances={Describe(MaxUserInstances.Value)}, " +
              $"max_user_watches={Describe(MaxUserWatches.Value)}; GitBench holds {Live} instances " +
              "and one watch per directory beneath each watched repository."
            : string.Empty;
        Report($"cannot watch '{repoPath}' for external changes ({cause}).{limits} " +
               "Changes made to this repository outside the app will not refresh on their own.");
    }

    private static string Describe(int? limit) => limit?.ToString() ?? "unknown";

    private static void Report(string message)
    {
        try { Console.Error.WriteLine($"[GitBench] {message}"); }
        catch { /* Diagnostics must never become the failure they report. */ }
    }

    private static int? ReadProcLimit(string name)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var text = File.ReadAllText($"/proc/sys/fs/inotify/{name}");
            return int.TryParse(text.Trim(), out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }
}

namespace GitBench.App;

/// <summary>
/// Records whether a launch of this version ever reached the run loop, so a build that dies during
/// startup can be recognised on the next launch and escaped via <see cref="RecoveryUpdater"/>.
/// <para>
/// A counter on disk rather than a <c>try</c>/<c>catch</c>, because the crashes worth surviving are
/// the ones a catch block never sees: a managed exception unwinding a native callback frame
/// fail-fasts under NativeAOT, as do segfaults and X/Wayland protocol errors.
/// </para>
/// </summary>
internal sealed class StartupHealth
{
    private const int CrashLoopThreshold = 2;

    private readonly string _path;

    private StartupHealth(string path, int failedLaunches)
    {
        _path = path;
        FailedLaunches = failedLaunches;
    }

    /// <summary>Launches of this version that died before the run loop started pumping.</summary>
    public int FailedLaunches { get; }

    public bool IsCrashLooping => FailedLaunches >= CrashLoopThreshold;

    /// <summary>
    /// Marks a launch as in progress and reports how many consecutive ones already failed. Call
    /// before anything that could take the process down, or the launch it fails to record is the
    /// one that mattered.
    /// </summary>
    public static StartupHealth BeginLaunch(string path, string version)
    {
        var failed = Read(path, version);
        Write(path, version, failed + 1);
        return new StartupHealth(path, failed);
    }

    /// <summary>The run loop is pumping — clears the count. Idempotent.</summary>
    public void MarkHealthy()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Startup health must never be the thing that stops the app from running.
        }
    }

    private static int Read(string path, string version)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            // Another version's failures say nothing about this one — whatever crashed, it isn't
            // the build about to run.
            if (lines.Length < 2 || lines[0] != version) return 0;
            return int.TryParse(lines[1], out var failed) ? failed : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void Write(string path, string version, int failedLaunches)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, $"{version}{Environment.NewLine}{failedLaunches}{Environment.NewLine}");
        }
        catch
        {
            // See MarkHealthy.
        }
    }
}

using System.Diagnostics;
using System.Text;

namespace GitBench.Git;

/// <summary>
/// The environment the user's own tools live in, as opposed to the one the app was launched with.
/// </summary>
/// <remarks>
/// A macOS app started from the Dock inherits a minimal <c>PATH</c> that has neither Homebrew nor
/// anything installed with cargo or npm in it, so every program the user installed themselves is
/// present on disk and invisible to the app. Asking the login shell for its environment, once, is
/// what makes git, git-lfs, credential helpers and language servers findable. Everywhere else the
/// process environment is already the user's, and this adds nothing.
/// </remarks>
internal static class LoginShellEnvironment
{
    private const string SnapshotMarker = "@@gitbench-env@@";
    private const int SnapshotTimeoutMs = 10_000;

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string>? _variables;
    private static IReadOnlyDictionary<string, string>? _forChildProcess;

    /// <summary>What the login shell says, and nothing else. Empty off macOS.</summary>
    public static IReadOnlyDictionary<string, string> Variables
    {
        get
        {
            if (_variables != null) return _variables;
            lock (Gate) return _variables ??= Resolve();
        }
    }

    /// <summary>
    /// What a child process should start with: this process's own environment, with the login
    /// shell's over the top. The overlay direction matters — the shell's <c>PATH</c> is the one
    /// that can see the user's tools.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForChildProcess
    {
        get
        {
            if (_forChildProcess != null) return _forChildProcess;
            lock (Gate) return _forChildProcess ??= Merge(Variables);
        }
    }

    /// <summary>The absolute path to a program on the login shell's <c>PATH</c>, or null when it is
    /// not there. Bare names only; a caller with an absolute path already has its answer.</summary>
    public static string? Find(string command)
    {
        if (!Variables.TryGetValue("PATH", out var path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> login)
    {
        var merged = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value)
                merged[key] = value;

        foreach (var (key, value) in login) merged[key] = value;
        return merged;
    }

    private static Dictionary<string, string> Resolve()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsMacOS()) return env;

        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"printf '%s' '{SnapshotMarker}'; env -0");

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardInput.Close();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (proc.WaitForExit(SnapshotTimeoutMs))
                {
                    ParseSnapshot(stdoutTask.GetAwaiter().GetResult(), env);
                    stderrTask.GetAwaiter().GetResult();
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { /* fall through to the PATH-only default */ }

        if (!env.ContainsKey("PATH"))
        {
            var current = Environment.GetEnvironmentVariable("PATH");
            var extras = new[] { "/opt/homebrew/bin", "/usr/local/bin" }
                .Where(p => current == null || !current.Split(':').Contains(p));
            env["PATH"] = string.Join(':', extras.Prepend(current ?? string.Empty).Where(s => s.Length > 0));
        }

        return env;
    }

    private static void ParseSnapshot(string output, Dictionary<string, string> env)
    {
        var start = output.LastIndexOf(SnapshotMarker, StringComparison.Ordinal);
        if (start < 0) return;

        foreach (var entry in output[(start + SnapshotMarker.Length)..].Split('\0'))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            env[entry[..eq]] = entry[(eq + 1)..];
        }
    }
}

using System.Diagnostics;
using System.Text;

namespace GitGui;

// Owns every git subprocess invocation: builds the start info, opens the activity scope so
// RepoWatcher drops the FSW events our own writes cause, spawns, drains both pipes
// concurrently (a full pipe buffer on either stream would otherwise deadlock), waits, and
// returns a uniform GitResult. Centralizing this is what guarantees the activity scope is
// opened for EVERY invocation — reads and mutations alike — rather than per call site, which
// previously left mutations (checkout/merge/reset/pull) outside the FSW-loop guard.
internal sealed class GitProcessRunner
{
    private readonly IRepoActivityTracker _activity;

    public GitProcessRunner(IRepoActivityTracker activity)
    {
        _activity = activity;
    }

    // Direct = the git executable straight (reads: fast, no shell, no auth needed).
    // Shell  = on macOS, the user's interactive login shell sources their rc files first so
    //          ssh-agent / credential helpers / Homebrew PATH are visible (mutations that may
    //          hit the network). On Windows/Linux both modes invoke git directly.
    public enum GitLaunch { Direct, Shell }

    public readonly record struct GitResult(int ExitCode, string Stdout, string Stderr, bool Started = true)
    {
        public bool Ok => Started && ExitCode == 0;

        // git often emits the real signal (CONFLICT lines, "Auto-merging X") on stdout while
        // stderr is empty or a stray newline; prefer stderr only when it carries something.
        public string PreferredStream => !string.IsNullOrWhiteSpace(Stderr) ? Stderr : Stdout;

        // Full error block — for callers that surface the error in a scrollable dialog where
        // the whole context (file lists, hints) is useful.
        public string BlockError(string commandLabel)
        {
            if (!Started) return "Failed to start git.";
            var msg = CombineGitOutput(Stderr, Stdout);
            return string.IsNullOrEmpty(msg) ? $"{commandLabel} exited with code {ExitCode}." : msg;
        }

        // Single most-relevant line — for callers that show the error in a one-line banner.
        public string FirstLineError(string commandLabel)
        {
            if (!Started) return "Failed to start git.";
            var msg = FirstMeaningfulLine(PreferredStream);
            return string.IsNullOrEmpty(msg) ? $"{commandLabel} exited with code {ExitCode}." : msg;
        }
    }

    public GitResult Run(
        string workingDir,
        IReadOnlyList<string> args,
        GitLaunch launch = GitLaunch.Shell,
        string? stdin = null,
        Action<ProcessStartInfo>? configure = null)
    {
        using var _ = _activity.Begin(workingDir);
        var psi = launch == GitLaunch.Direct ? BuildDirectPsi(workingDir, args) : BuildShellPsi(args, workingDir);
        if (stdin != null) psi.RedirectStandardInput = true;
        configure?.Invoke(psi);

        using var proc = Process.Start(psi);
        if (proc == null) return new GitResult(-1, string.Empty, string.Empty, Started: false);

        // Read both streams concurrently so a full pipe buffer on either side can't deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
        }
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new GitResult(proc.ExitCode, stdout, stderr);
    }

    // Streaming variant for long-running network ops (fetch) that surface progress line by
    // line. Captures everything for post-hoc error extraction and forwards each line live.
    public (int ExitCode, string Captured, bool Started) RunStreaming(
        string workingDir,
        IReadOnlyList<string> args,
        Action<string>? onLine)
    {
        using var _ = _activity.Begin(workingDir);
        var psi = BuildShellPsi(args, workingDir);

        using var proc = Process.Start(psi);
        if (proc == null) return (-1, string.Empty, false);

        var captured = new StringBuilder();
        void Handle(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (captured) captured.AppendLine(line);
            onLine?.Invoke(line);
        }
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        string text;
        lock (captured) text = captured.ToString();
        return (proc.ExitCode, text, true);
    }

    // ────────── process start info ──────────

    private static ProcessStartInfo BuildDirectPsi(string workingDir, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitExecutable(),
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // On macOS, GUI apps launched outside a terminal (Finder, IDE, Launch Services) don't
    // inherit the user's interactive-shell environment. Anything set up in .zshrc / .bashrc —
    // 1Password's SSH_AUTH_SOCK, manually-started ssh-agent, the Homebrew PATH,
    // GIT_SSH_COMMAND overrides — is invisible to the child process, and `git push` over SSH
    // dies with "Could not read from remote repository". Running git through the user's shell
    // with `-i -c` sources their rc files first so ssh and git see the same environment they
    // do in Terminal. Each user-typed arg is shell-quoted so spaces or metacharacters can't
    // break the command or inject extra ones.
    private static ProcessStartInfo BuildShellPsi(IReadOnlyList<string> gitArgs, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        if (OperatingSystem.IsMacOS())
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";
            psi.FileName = shell;
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-c");
            var sb = new StringBuilder("GIT_TERMINAL_PROMPT=0 git");
            foreach (var a in gitArgs)
            {
                sb.Append(' ');
                sb.Append(SingleQuoteShellArg(a));
            }
            psi.ArgumentList.Add(sb.ToString());
        }
        else
        {
            psi.FileName = "git";
            foreach (var a in gitArgs) psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static string SingleQuoteShellArg(string s)
        => "'" + s.Replace("'", "'\\''") + "'";

    // macOS GUI apps launched outside a terminal don't inherit the user's shell PATH, so
    // Homebrew git (/opt/homebrew/bin/git, /usr/local/bin/git) is invisible to a bare
    // Process.Start("git"). Ask the login shell where git lives, once, and reuse the
    // absolute path everywhere.
    private static string? _gitExecutable;
    private static readonly object _gitExecutableLock = new();

    private static string GitExecutable()
    {
        if (_gitExecutable != null) return _gitExecutable;
        lock (_gitExecutableLock)
        {
            _gitExecutable ??= ResolveGitExecutable();
            return _gitExecutable;
        }
    }

    private static string ResolveGitExecutable()
    {
        if (!OperatingSystem.IsMacOS()) return "git";

        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("command -v git");
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var path = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (path.Length > 0 && File.Exists(path)) return path;
            }
        }
        catch { /* fall through to defaults */ }

        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;

        return "git";
    }

    // ────────── error-text extraction ──────────

    // Picks the meaningful block out of git's two streams. Rules, in priority order:
    //   1. If either stream carries an `error:` / `fatal:` / `hint:` prefix, that stream is
    //      authoritative — use just its extracted block. Don't dilute it with the other
    //      stream's content, which is typically the noisy `git status` recap a stash/merge
    //      op runs after the failure ("On branch …", "Changes not staged for commit", etc.).
    //   2. Otherwise prefer stdout — operations like `git stash apply` with conflicts emit
    //      the actual signal (CONFLICT lines, "Auto-merging X") on stdout while stderr is
    //      empty or a stray `\n`.
    //   3. Fall back to stderr if stdout is whitespace-only.
    public static string CombineGitOutput(string stderr, string stdout)
    {
        if (HasGitPrefix(stderr)) return ExtractGitErrorBlock(stderr);
        if (HasGitPrefix(stdout)) return ExtractGitErrorBlock(stdout);
        if (!string.IsNullOrWhiteSpace(stdout)) return ExtractGitErrorBlock(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) return ExtractGitErrorBlock(stderr);
        return string.Empty;
    }

    private static bool HasGitPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Pulls the most relevant single line out of a git error blob — typically the
    // "fatal: …" / "error: …" line near the end. Used by callers that show the error in a
    // single-line banner (ErrorBar) where multi-line text would overflow.
    public static string FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return text.Trim();
    }

    // Returns the full meaningful error block from a git error blob — starting at the first
    // "error:" / "fatal:" / "hint:" line and including everything after it (the indented file
    // list under "would be overwritten by merge:", the "Please commit your changes or stash
    // them" hint, etc.). Used by callers that surface the error in a scrollable dialog where
    // the full context is useful.
    public static string ExtractGitErrorBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split('\n');
        var startIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            {
                startIdx = i;
                break;
            }
        }

        IEnumerable<string> kept;
        if (startIdx < 0)
        {
            // No git-prefixed line — fall back to all non-empty lines, trimmed.
            kept = lines.Select(l => l.TrimEnd()).Where(l => l.Length > 0);
        }
        else
        {
            kept = lines.Skip(startIdx).Select(l => l.TrimEnd());
        }

        var result = string.Join("\n", kept).TrimEnd();
        return result.Length > 0 ? result : text.Trim();
    }

    public static string AugmentCredentialError(string headline, string fullOutput)
    {
        if (string.IsNullOrEmpty(headline) || string.IsNullOrEmpty(fullOutput))
            return headline;
        var looksLikeAuth = fullOutput.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || fullOutput.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || fullOutput.Contains("could not read Password", StringComparison.OrdinalIgnoreCase)
            || fullOutput.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeAuth) return headline;

        var hint = OperatingSystem.IsMacOS()
            ? "Configure a credential helper (e.g. `git config --global credential.helper osxkeychain`) or switch the remote to SSH."
            : OperatingSystem.IsWindows()
                ? "Configure a credential helper (Git Credential Manager ships with Git for Windows) or switch the remote to SSH."
                : "Configure a credential helper (e.g. `git config --global credential.helper store`) or switch the remote to SSH.";
        return $"{headline}\n\n{hint}";
    }
}

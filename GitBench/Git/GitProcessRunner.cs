using System.Diagnostics;
using System.Text;
using GitBench.Features.Repos;

namespace GitBench.Git;

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

    // Identity injection seam. When set, every invocation with inject:true gets the resolver's
    // `-c key=value` args (user.name/email, core.sshCommand, signing) PREPENDED before the
    // subcommand, so the right Git identity is applied per-repo without ever writing the repo's
    // config. The resolver's OWN git reads (remote URL, local config) must pass inject:false so
    // it can't recurse back into itself. Resolution is cheap (memoized in GitIdentityService);
    // the resolver never blocks on git here after the first read per repo.
    public Func<string, IReadOnlyList<string>>? IdentityPrefixResolver { get; set; }

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
        string? stdin = null,
        Action<ProcessStartInfo>? configure = null,
        bool inject = true)
    {
        // Resolve identity prefix BEFORE opening the activity scope: the resolver's own git
        // reads (inject:false) open their own nested scopes, and we want those cleanly separate
        // from this op's scope.
        var prefix = inject ? IdentityPrefixResolver?.Invoke(workingDir) : null;
        using var _ = _activity.Begin(workingDir);
        var psi = BuildPsi(workingDir, args, prefix);
        if (stdin != null)
        {
            psi.RedirectStandardInput = true;
            // Write patches as UTF-8 (no BOM) so a hunk touching CJK lines applies byte-for-byte.
            psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
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

    // Raw-bytes variant for blob reads whose content isn't text. Run() decodes stdout as UTF-8,
    // which replaces every invalid byte sequence with U+FFFD — fine for patches and file text,
    // fatal for a PNG. This drains the raw pipe instead. Output past `maxBytes` is read and
    // discarded (the pipe must keep draining or git blocks forever) and reported as truncated,
    // so a huge blob can't be pulled into memory whole.
    public (int ExitCode, byte[] Stdout, bool Started, bool Truncated) RunBytes(
        string workingDir,
        IReadOnlyList<string> args,
        int maxBytes,
        bool inject = true)
    {
        var prefix = inject ? IdentityPrefixResolver?.Invoke(workingDir) : null;
        using var _ = _activity.Begin(workingDir);
        var psi = BuildPsi(workingDir, args, prefix);

        using var proc = Process.Start(psi);
        if (proc == null) return (-1, [], false, false);

        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var captured = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var truncated = false;
        var stdout = proc.StandardOutput.BaseStream;
        int read;
        while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (truncated) continue;
            if (captured.Length + read > maxBytes) truncated = true;
            else captured.Write(buffer, 0, read);
        }
        proc.WaitForExit();
        stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, truncated ? [] : captured.ToArray(), true, truncated);
    }

    // Streaming variant for long-running network ops (fetch) that surface progress line by
    // line. Captures everything for post-hoc error extraction and forwards each line live.
    public (int ExitCode, string Captured, bool Started) RunStreaming(
        string workingDir,
        IReadOnlyList<string> args,
        Action<string>? onLine,
        bool inject = true)
    {
        var prefix = inject ? IdentityPrefixResolver?.Invoke(workingDir) : null;
        using var _ = _activity.Begin(workingDir);
        var psi = BuildPsi(workingDir, args, prefix);

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

    // Start info for a long-lived plumbing process the caller drives itself — see GitBlobReader,
    // which keeps a `cat-file --batch` alive per repo. Same executable and environment as every
    // other invocation, plus stdin so requests can be fed in. No identity prefix and
    // no activity scope: neither applies to a process that only ever reads objects, and holding a
    // scope open for the process's whole life would suppress the FSW events we do want.
    public ProcessStartInfo BuildLongRunningPsi(string workingDir, IReadOnlyList<string> args)
    {
        var psi = BuildPsi(workingDir, args);
        psi.RedirectStandardInput = true;
        return psi;
    }

    // ────────── process start info ──────────

    private static ProcessStartInfo BuildPsi(string workingDir, IReadOnlyList<string> args, IReadOnlyList<string>? prefix = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitExecutable(),
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Git emits UTF-8 (diff bodies are the file's raw bytes); without this .NET decodes
            // with the OS default code page (CP1252 on Windows), which turns CJK into mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Even with git itself resolved to an absolute path, git spawns helpers (git-lfs for
        // the filter-process, credential helpers) via PATH — the GUI app's bare PATH lacks
        // /usr/local/bin & /opt/homebrew/bin, which surfaces as "git-lfs: command not found" +
        // "the remote end hung up unexpectedly" on every status/diff in an LFS repo.
        foreach (var (key, value) in LoginShellEnvironment.Variables) psi.Environment[key] = value;
        foreach (var key in UninheritedEnvKeys) psi.Environment.Remove(key);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Identity `-c key=value` overrides must come before the subcommand.
        if (prefix != null) foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // macOS GUI apps launched outside a terminal don't inherit the user's shell PATH, so
    // Homebrew git (/opt/homebrew/bin/git, /usr/local/bin/git) is invisible to a bare
    // Process.Start("git"). Find it on the login shell's PATH, once, and reuse the
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
        if (LoginShellEnvironment.Find("git") is { } found) return found;

        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;

        return "git";
    }

    // `--pathspec-from-file` landed in 2.25 for add/checkout/reset/restore and 2.26 for
    // stash push and rm; gating everything on 2.26 keeps bulk path ops on a single branch.
    private static bool? _supportsPathspecFromFile;
    private static readonly object _pathspecSupportLock = new();

    public static bool SupportsPathspecFromFile
    {
        get
        {
            if (_supportsPathspecFromFile is { } cached) return cached;
            lock (_pathspecSupportLock)
            {
                return _supportsPathspecFromFile ??= DetectPathspecFromFileSupport();
            }
        }
    }

    private static bool DetectPathspecFromFileSupport()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GitExecutable(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\.(\d+)");
            if (!match.Success) return false;
            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            return major > 2 || (major == 2 && minor >= 26);
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] UninheritedEnvKeys =
    {
        "PWD", "OLDPWD", "SHLVL", "_",
        "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_PREFIX", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE",
    };

    // ────────── error-text extraction ──────────

    // Full error text for the scrollable dialog: keep BOTH streams (cause and headline are
    // often split across them), prefixed stream first. One-line banner uses FirstLineError.
    public static string CombineGitOutput(string stderr, string stdout)
    {
        var errBlock = CleanStream(stderr);
        var outBlock = CleanStream(stdout);
        if (string.IsNullOrEmpty(errBlock)) return outBlock;
        if (string.IsNullOrEmpty(outBlock)) return errBlock;
        if (errBlock == outBlock) return errBlock;

        return HasGitPrefix(stderr) || !HasGitPrefix(stdout)
            ? errBlock + "\n\n" + outBlock
            : outBlock + "\n\n" + errBlock;
    }

    // Every non-blank line of a stream, collapsing \r progress to its final state.
    private static string CleanStream(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var kept = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var cr = line.LastIndexOf('\r');
            if (cr >= 0) line = line[(cr + 1)..];
            line = line.TrimEnd();
            if (line.Length > 0) kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    private static bool HasGitPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var line in text.Split('\n'))
        {
            if (IsErrorBlockStart(line.TrimStart())) return true;
        }
        return false;
    }

    // Lines that mark the start of the meaningful error block. Besides git's own
    // "error:"/"fatal:"/"hint:"/"warning:" prefixes, push failures carry the actual reason on
    // lines that come *before* the generic "error: failed to push some refs": the "! [rejected]"
    // / "! [remote rejected]" status summary and "remote:" messages relayed from the server
    // (permission denied, pre-receive hook output). Anchoring on those keeps the real cause in
    // the block instead of trimming it away above the first "error:" line.
    private static bool IsErrorBlockStart(string trimmed)
        => trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("!");

    // The complaint at the end of an otherwise-successful run: everything from the first
    // error/warning/hint/remote line onward, else the last non-empty line. Unlike
    // FirstMeaningfulLine this keeps the whole block — hook output is usually several lines and
    // often carries no git prefix at all, so the last line is the only anchor there is.
    public static string ErrorTail(string text)
    {
        var cleaned = CleanStream(text);
        if (cleaned.Length == 0) return string.Empty;
        var lines = cleaned.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (IsErrorBlockStart(lines[i].TrimStart()))
                return string.Join("\n", lines[i..]);
        return lines[^1];
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
            if (IsErrorBlockStart(lines[i].TrimStart()))
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

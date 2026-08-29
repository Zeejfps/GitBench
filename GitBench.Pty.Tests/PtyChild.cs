using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// The children these tests spawn, named for what the child is asked to do rather than for the shell
/// that does it. Each intent dispatches to a Windows shell or to <c>/bin/sh</c>, and both arms print
/// the same text, so one assertion pins the contract on both platforms.
/// </summary>
/// <remarks>
/// <para>
/// Programs are addressed by bare name so that PATH resolution is exercised too, and Unix children
/// stay inside POSIX — <c>sh</c>, <c>printf</c>, <c>env</c>, <c>awk</c>, <c>stty</c>, <c>pwd</c>,
/// <c>tty</c>, <c>nohup</c>, and <c>sleep</c> with whole seconds only — because the same children
/// have to run on Linux.
/// </para>
/// <para>
/// A variable is read out of the process environment with <c>env</c> rather than as a shell variable.
/// <c>/bin/sh</c> on macOS is bash, and bash gives itself a TERM of its own when the environment
/// carries none: <c>env -i /bin/sh -c 'echo ${TERM-unset}'</c> prints <c>dumb</c> while
/// <c>env -i /bin/sh -c env</c> shows no TERM at all. Asked through parameter expansion, the test for
/// terminal identity would be a test of the shell and would report a failure the session is not
/// responsible for.
/// </para>
/// </remarks>
static class PtyChild
{
    /// <summary>How long a test waits on a real process before calling it a failure.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long teardown may take. Shorter than <see cref="Patience"/> on purpose, and not an
    /// arbitrary tightening: <c>TerminalSession.Dispose</c> joins its reader for two seconds and then
    /// gives up, so a session that releases a blocked reader only once some unrelated timeout expires
    /// leaks that thread for the life of the app while still passing a thirty-second bound.
    /// </summary>
    public static readonly TimeSpan TeardownPatience = TimeSpan.FromSeconds(5);

    /// <summary>Printed by every child that has to be alive before a test can do anything to it.</summary>
    public const string Ready = "[ready]";

    /// <summary>
    /// What a terminal sends for the Enter key. The Windows console wants the pair; the Unix line
    /// discipline turns a bare carriage return into a newline itself, and sending both would submit a
    /// blank second line.
    /// </summary>
    public static string Enter => OperatingSystem.IsWindows() ? "\r\n" : "\r";

    /// <summary>The name of a program that is not installed anywhere on this platform.</summary>
    public static string MissingExecutable =>
        OperatingSystem.IsWindows() ? "gitbench-no-such-program.exe" : "gitbench-no-such-program";

    public static IPtySession Start(PtySessionOptions options) => new PtySessionFactory().Start(options);

    /// <summary>Types a line at a child that is sitting at a prompt.</summary>
    public static void Type(IPtySession session, string line) =>
        session.WriteInput(Encoding.UTF8.GetBytes(line + Enter));

    /// <summary>
    /// Layers one variable over whatever an intent already set, rather than replacing the overlay the
    /// way <c>with { Environment = ... }</c> would — an intent such as <see cref="Shell"/> carries an
    /// overlay of its own that a test still needs.
    /// </summary>
    public static PtySessionOptions WithVariable(this PtySessionOptions options, string name, string? value)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (existing, existingValue) in options.Environment)
            environment[existing] = existingValue;

        environment[name] = value;

        return options with { Environment = environment };
    }

    /// <summary>The shell line that prints one variable as <c>[NAME=value]</c> when typed at a prompt.</summary>
    public static string EchoVariable(string name) =>
        OperatingSystem.IsWindows() ? $"echo [{name}=%{name}%]" : $"echo \"[{name}=${name}]\"";

    /// <summary>The shell line that ends a shell with a chosen status when typed at a prompt.</summary>
    public static string Exit(int code) => $"exit {code}";

    /// <summary>
    /// Waits for the shell to offer a prompt, which is the only point at which typing at it means
    /// anything. Both shells are configured so the prompt carries <see cref="TempDirectory.Token"/>.
    /// </summary>
    public static void WaitForPrompt(PtyOutputReader output, TempDirectory work) =>
        Assert.True(
            output.WaitFor(work.Token, Patience),
            $"The shell never printed a prompt, so there was nothing to type at. Terminal showed:\n{output.Describe()}");

    /// <summary>Runs <paramref name="executable"/> with no arguments, for the spawns that have to fail.</summary>
    public static PtySessionOptions At(TempDirectory work, string executable) => new()
    {
        Executable = executable,
        WorkingDirectory = work.Path,
    };

    /// <summary>
    /// A child that reports whether each of its three standard descriptors is a terminal, and how
    /// large that terminal is, as <c>[tty=yesyesyes;cols=C;rows=R]</c>.
    /// </summary>
    /// <remarks>
    /// All three descriptors rather than just standard output: an implementation that dups the slave
    /// onto 0 and 1 and forgets 2 passes the narrower question, and then every diagnostic the shell
    /// writes vanishes.
    /// </remarks>
    public static PtySessionOptions ReportsTerminal(TempDirectory work, PtySize size) =>
        (OperatingSystem.IsWindows()
            ? PowerShell(work, WindowsTerminalReport)
            : Sh(work, UnixTerminalReport)) with { Size = size };

    /// <summary>
    /// A child that reports whether it has a controlling terminal and whether it is in the foreground
    /// process group, which is what a session leader opening the slave without O_NOCTTY buys and what
    /// job control and Ctrl-C depend on.
    /// </summary>
    public static PtySessionOptions ReportsControllingTerminal(TempDirectory work)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Only Unix has a controlling terminal to report.");

        return Sh(work, UnixControllingTerminalReport);
    }

    /// <summary>A child that prints the directory it was started in, as <c>[cwd=...]</c>.</summary>
    public static PtySessionOptions PrintsWorkingDirectory(TempDirectory work) =>
        OperatingSystem.IsWindows()
            ? Cmd(work, "/c", "echo [cwd=%CD%]")
            : Sh(work, UnixPrintWorkingDirectory);

    /// <summary>
    /// A child that prints one line per name, <c>[NAME=value]</c>, or <c>[NAME=unset]</c> when the
    /// variable is not in its environment at all. An empty variable prints as <c>[NAME=]</c>, so a
    /// removal and an empty value are told apart — the distinction the old
    /// <c>[%GITBENCH_PTY_REMOVED%]</c> assertion could not make.
    /// </summary>
    public static PtySessionOptions PrintsVariables(TempDirectory work, params string[] names) =>
        OperatingSystem.IsWindows()
            ? PowerShell(work, WindowsVariableReport(names))
            : Sh(work, UnixVariableReport, names);

    /// <summary>
    /// A child that prints the PATH it was given, as <c>[PATH=...]</c>, using only what its shell
    /// carries built in.
    /// </summary>
    /// <remarks>
    /// <see cref="PrintsVariables"/> cannot answer this one. It reports through <c>env</c> and
    /// <c>awk</c>, and the only test that asks about PATH is the test that overlays PATH with a
    /// directory holding neither — so the reporter would be the thing that broke, and the failure
    /// would look like the session losing the variable it had in fact delivered. Parameter expansion
    /// is not the general reporter for the reason that class documents, but it needs nothing on PATH
    /// to find, which is the whole of what this asks for.
    /// </remarks>
    public static PtySessionOptions PrintsThePath(TempDirectory work) =>
        OperatingSystem.IsWindows()
            ? Cmd(work, "/c", "echo [PATH=%PATH%]")
            : Sh(work, UnixPrintThePath);

    /// <summary>
    /// A child that prints its arguments one bracketed entry each, as <c>[argv=&lt;a&gt;|&lt;b&gt;|]</c>,
    /// so an entry that arrived empty is still visible.
    /// </summary>
    public static PtySessionOptions PrintsArguments(TempDirectory work, params string[] arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var powerShellScript = work.File("argv.ps1", WindowsArgvScript);

            return new()
            {
                Executable = "powershell.exe",
                Arguments = new[]
                    {
                        "-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", powerShellScript,
                    }
                    .Concat(arguments)
                    .ToArray(),
                WorkingDirectory = work.Path,
            };
        }

        var shellScript = work.File("argv.sh", UnixArgvScript);

        return new()
        {
            Executable = "sh",
            Arguments = new[] { shellScript }.Concat(arguments).ToArray(),
            WorkingDirectory = work.Path,
        };
    }

    /// <summary>A child that prints one line to standard output and leaves.</summary>
    public static PtySessionOptions Prints(TempDirectory work, string text) =>
        OperatingSystem.IsWindows() ? Cmd(work, "/c", "echo", text) : Sh(work, UnixPrint, text);

    /// <summary>A child that prints one line to standard error and leaves.</summary>
    public static PtySessionOptions PrintsToStandardError(TempDirectory work, string text) =>
        OperatingSystem.IsWindows()
            ? Cmd(work, "/c", $"echo {text} 1>&2")
            : Sh(work, UnixPrintToStandardError, text);

    /// <summary>A child that exits immediately with a chosen status.</summary>
    public static PtySessionOptions ExitsWith(TempDirectory work, int code) =>
        OperatingSystem.IsWindows()
            ? Cmd(work, "/c", "exit", "/b", code.ToString())
            : Sh(work, $"exit {code}");

    /// <summary>
    /// A child that ends itself with an uncatchable signal, so the platform records which signal it
    /// was rather than an exit code.
    /// </summary>
    public static PtySessionOptions KillsItself(TempDirectory work)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Only Unix records a terminating signal.");

        return Sh(work, "kill -9 $$");
    }

    /// <summary>
    /// A child that announces itself, waits for the terminal to change size underneath it, and then
    /// reports the size it ended up with.
    /// </summary>
    public static PtySessionOptions WatchesForResize(TempDirectory work, PtySize size) =>
        (OperatingSystem.IsWindows()
            ? PowerShell(work, WindowsResizeWatch)
            : Sh(work, UnixResizeWatch)) with { Size = size };

    /// <summary>
    /// A shell sitting at a prompt, waiting to be typed at and outliving the test unless something
    /// ends it. Its prompt carries <see cref="TempDirectory.Token"/> on both platforms — cmd prints
    /// the working directory, and sh is given a PS1 that says the same thing.
    /// </summary>
    /// <remarks>
    /// ENV and BASH_ENV are removed so that a developer's own rc file cannot print into an assertion.
    /// </remarks>
    public static PtySessionOptions Shell(TempDirectory work)
    {
        if (OperatingSystem.IsWindows())
            return Cmd(work);

        var options = new PtySessionOptions
        {
            Executable = "sh",
            Arguments = ["-i"],
            WorkingDirectory = work.Path,
        };

        return options
            .WithVariable("PS1", $"[{work.Token}]$ ")
            .WithVariable("ENV", null)
            .WithVariable("BASH_ENV", null);
    }

    /// <summary>
    /// A child that prints <see cref="Ready"/>, then goes quiet and stays alive without printing
    /// anything else.
    /// </summary>
    /// <remarks>
    /// The one child that makes "a reader blocked in read" a fact rather than a hope: once
    /// <see cref="Ready"/> has been consumed there is provably nothing left for the reader to come
    /// back with, so the next call is blocked until the session ends it.
    /// </remarks>
    public static PtySessionOptions SitsSilently(TempDirectory work) =>
        OperatingSystem.IsWindows()
            ? PowerShell(work, "Write-Host '[ready]'; Start-Sleep -Seconds 600")
            : Sh(work, UnixSitSilently);

    /// <summary>
    /// A child that prints <see cref="Ready"/> and then goes on reading its input for as long as it
    /// is given any, discarding what it reads.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="SitsSilently"/>, and the only child a test of writing may use.
    /// A terminal's input queue is about a kilobyte — measured here, and the same with no session
    /// involved at all — so a write that outruns it blocks until somebody reads, which is the line
    /// discipline applying backpressure and not the session doing anything wrong. Against a child
    /// that never reads, any test that writes more than that much is asserting that the kernel does
    /// not do its job.
    /// </remarks>
    public static PtySessionOptions ReadsContinuously(TempDirectory work) =>
        OperatingSystem.IsWindows()
            ? PowerShell(work, "Write-Host '[ready]'; while ($null -ne [Console]::In.ReadLine()) { }")
            : Sh(work, UnixReadContinuously);

    /// <summary>A child that prints <see cref="Ready"/>, reads one line, then prints <c>[typed=that line]</c>.</summary>
    public static PtySessionOptions ReadsOneLine(TempDirectory work) =>
        OperatingSystem.IsWindows()
            ? PowerShell(work, "Write-Host '[ready]'; $l = [Console]::ReadLine(); Write-Host \"[typed=$l]\"")
            : Sh(work, UnixReadOneLine);

    /// <summary>
    /// A child that prints <see cref="Ready"/> and then copies its input back out with the line
    /// discipline switched off.
    /// </summary>
    /// <remarks>
    /// Raw mode is the point: a canonical-mode terminal discards a line longer than MAX_CANON
    /// outright — measured here, four thousand bytes written as one line arrived as none of them — so
    /// a test of a bulk write has to take the line editor out of the picture or it measures the
    /// kernel rather than <see cref="IPtySession.WriteInput"/>.
    /// </remarks>
    public static PtySessionOptions EchoesRaw(TempDirectory work)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Raw mode is set with stty, which is POSIX.");

        return Sh(work, UnixEchoRaw);
    }

    /// <summary>
    /// A child that leaves behind a grandchild which ignores SIGHUP and holds the terminal, writes
    /// that grandchild's pid to <paramref name="pidPath"/>, prints <c>[grandchild-ready]</c>, and
    /// then stays alive itself.
    /// </summary>
    /// <remarks>
    /// This is the child that tells the two teardown designs apart, and the <c>nohup</c> is what makes
    /// it discriminating. Measured on this machine: with the grandchild nohuped, ending only the
    /// direct child leaves it running while ending the whole process group ends it too. Without
    /// nohup, macOS revokes the terminal when the session leader dies and cleans up either way, so
    /// the test proves nothing here — and Linux, which never cleans up on its own, is exactly where
    /// the bug would then be waiting.
    /// </remarks>
    public static PtySessionOptions LeavesADetachedGrandchild(TempDirectory work, string pidPath)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process groups and SIGHUP are POSIX.");

        return Sh(work, UnixDetachedGrandchild).WithVariable("GITBENCH_PTY_PID", pidPath);
    }

    /// <summary>
    /// A child that writes the name of the terminal it is running on into <paramref name="outputPath"/>,
    /// prints <c>[recorded]</c>, then stays alive.
    /// </summary>
    /// <remarks>
    /// The name goes to a file rather than the stream because two sessions running at once have to be
    /// compared for inequality, and <see cref="VtText.Contains"/> only answers containment, case
    /// insensitively at that. The marker is printed after the write, so seeing it means the file is
    /// complete.
    /// </remarks>
    public static PtySessionOptions RecordsTerminalName(TempDirectory work, string outputPath)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Only Unix names its terminals in the filesystem.");

        return Sh(work, UnixRecordTerminalName).WithVariable("GITBENCH_PTY_OUT", outputPath);
    }

    /// <summary>
    /// A child that writes its own pid to <paramref name="pidPath"/>, prints <c>[recorded]</c>, then
    /// stays alive.
    /// </summary>
    /// <remarks>
    /// For telling "killed" apart from "killed and reaped": an unreaped child still occupies its pid,
    /// so <c>kill(pid, 0)</c> succeeds for a zombie and every other teardown assertion is satisfied by
    /// a session that signals and walks away.
    /// </remarks>
    public static PtySessionOptions RecordsItsOwnPid(TempDirectory work, string pidPath)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Only Unix leaves a zombie behind.");

        return Sh(work, UnixRecordOwnPid).WithVariable("GITBENCH_PTY_PID", pidPath);
    }

    /// <summary>A program that is not installed, for the spawn that has to fail.</summary>
    public static PtySessionOptions NoSuchProgram(TempDirectory work) => At(work, MissingExecutable);

    /// <summary>A file that exists and is not executable, which POSIX refuses with EACCES.</summary>
    public static PtySessionOptions NotExecutable(TempDirectory work)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Only Unix carries an executable bit.");

        return At(work, work.File("not-executable", "this is not a program\n"));
    }

    static PtySessionOptions Cmd(TempDirectory work, params string[] arguments) => new()
    {
        Executable = "cmd.exe",
        Arguments = arguments,
        WorkingDirectory = work.Path,
    };

    static PtySessionOptions PowerShell(TempDirectory work, string command) => new()
    {
        Executable = "powershell.exe",
        Arguments = ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", command],
        WorkingDirectory = work.Path,
    };

    /// <remarks>
    /// The extra <c>sh</c> fills the argv[0] slot <c>sh -c</c> reserves, so <paramref name="arguments"/>
    /// arrive as <c>$1</c> onward and text a test chose is never spliced into the script itself.
    /// </remarks>
    static PtySessionOptions Sh(TempDirectory work, string script, params string[] arguments) => new()
    {
        Executable = "sh",
        Arguments = new[] { "-c", script, "sh" }.Concat(arguments).ToArray(),
        WorkingDirectory = work.Path,
    };

    static string WindowsVariableReport(string[] names)
    {
        var list = string.Join(",", names.Select(name => $"'{name}'"));

        return $"foreach ($n in @({list})) {{ "
            + "$v = [Environment]::GetEnvironmentVariable($n); "
            + "if ($null -eq $v) { $v = 'unset' }; "
            + "Write-Host ('[' + $n + '=' + $v + ']') }";
    }

    const string WindowsTerminalReport =
        "$i = 'no'; if (-not [Console]::IsInputRedirected) { $i = 'yes' }; "
        + "$o = 'no'; if (-not [Console]::IsOutputRedirected) { $o = 'yes' }; "
        + "$e = 'no'; if (-not [Console]::IsErrorRedirected) { $e = 'yes' }; "
        + "Write-Host ('[tty=' + $i + $o + $e + ';cols=' + [Console]::WindowWidth "
        + "+ ';rows=' + [Console]::WindowHeight + ']')";

    const string WindowsResizeWatch =
        "Write-Host '[ready]'; "
        + "$w = [Console]::WindowWidth; $n = 0; "
        + "while ([Console]::WindowWidth -eq $w -and $n -lt 200) { Start-Sleep -Milliseconds 50; $n = $n + 1 }; "
        + "Write-Host ('[resized=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight + ']')";

    const string WindowsArgvScript =
        "$o = ''; foreach ($a in $args) { $o = $o + '<' + $a + '>|' }; Write-Host ('[argv=' + $o + ']')\n";

    const string UnixTerminalReport =
        """
        i=no; o=no; e=no
        [ -t 0 ] && i=yes
        [ -t 1 ] && o=yes
        [ -t 2 ] && e=yes
        set -- $(stty size)
        printf '[tty=%s%s%s;cols=%s;rows=%s]\n' "$i" "$o" "$e" "$2" "$1"
        """;

    const string UnixControllingTerminalReport =
        """
        if : < /dev/tty 2>/dev/null; then c=yes; else c=no; fi
        case "$(ps -o stat= -p $$)" in
            *+*) f=yes ;;
            *) f=no ;;
        esac
        printf '[ctty=%s;foreground=%s;device=%s]\n' "$c" "$f" "$(tty)"
        """;

    const string UnixPrintThePath =
        """
        echo "[PATH=$PATH]"
        """;

    const string UnixPrintWorkingDirectory =
        """
        printf '[cwd=%s]\n' "$(pwd)"
        """;

    const string UnixPrint =
        """
        printf '%s\n' "$1"
        """;

    const string UnixPrintToStandardError =
        """
        printf '%s\n' "$1" >&2
        """;

    const string UnixVariableReport =
        """
        show() {
            env | awk -v n="$1" 'index($0, n "=") == 1 { print "[" $0 "]"; f = 1 } END { if (!f) print "[" n "=unset]" }'
        }
        for v in "$@"; do show "$v"; done
        """;

    const string UnixResizeWatch =
        """
        printf '[ready]\n'
        first=$(stty size)
        n=0
        while [ "$(stty size)" = "$first" ] && [ "$n" -lt 25 ]; do sleep 1; n=$((n + 1)); done
        set -- $(stty size)
        printf '[resized=%sx%s]\n' "$2" "$1"
        """;

    const string UnixSitSilently =
        """
        printf '[ready]\n'
        n=0
        while [ "$n" -lt 600 ]; do sleep 1; n=$((n + 1)); done
        """;

    const string UnixReadOneLine =
        """
        printf '[ready]\n'
        read line
        printf '[typed=%s]\n' "$line"
        """;

    const string UnixReadContinuously =
        """
        stty raw -echo
        printf '[ready]\n'
        cat > /dev/null
        """;

    const string UnixEchoRaw =
        """
        stty raw -echo
        printf '[ready]\n'
        cat
        """;

    const string UnixDetachedGrandchild =
        """
        nohup /bin/sh -c 'printf "%s" "$$" > "$GITBENCH_PTY_PID"; n=0; while [ "$n" -lt 600 ]; do sleep 1; n=$((n + 1)); done' >/dev/null 2>&1 &
        while [ ! -s "$GITBENCH_PTY_PID" ]; do sleep 1; done
        printf '[grandchild-ready]\n'
        n=0
        while [ "$n" -lt 600 ]; do sleep 1; n=$((n + 1)); done
        """;

    const string UnixRecordOwnPid =
        """
        printf '%s' "$$" > "$GITBENCH_PTY_PID"
        printf '[recorded]\n'
        n=0
        while [ "$n" -lt 600 ]; do sleep 1; n=$((n + 1)); done
        """;

    const string UnixRecordTerminalName =
        """
        tty > "$GITBENCH_PTY_OUT"
        printf '[recorded]\n'
        n=0
        while [ "$n" -lt 600 ]; do sleep 1; n=$((n + 1)); done
        """;

    const string UnixArgvScript =
        """
        out=''
        for a in "$@"; do out="$out<$a>|"; done
        printf '[argv=%s]\n' "$out"

        """;
}

using GitBench.Pty;
using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

/// <summary>Which language the shell in the pane speaks, for the one caller that has to write a
/// command into it rather than a keystroke.</summary>
internal enum ShellFamily
{
    Posix,
    PowerShell,
    CommandProcessor,
}

/// <summary>
/// What to run in a terminal pane: the user's interactive shell, started in a repository, told what
/// kind of terminal it is talking to.
/// </summary>
/// <remarks>
/// The identity variables are set here rather than left to the session because
/// <see cref="PtySessionOptions"/> deliberately owns none of them, and they are not decoration: a
/// program decides whether it may use 256 colours and 24-bit colour by reading them, so a shell
/// started without them renders a duller screen than the one this pane can draw.
/// </remarks>
internal static class ShellCommand
{
    const string Acquirer = "/bin/sh";

    const string AcquireAndExec = "exec \"$0\" \"$@\"";

    /// <summary>
    /// The shell's own name, for a tab to wear until a program running in it says otherwise.
    /// The executable that was asked for rather than the acquirer that starts it: <c>/bin/sh</c> is
    /// how the terminal is taken, not what the user is typing into.
    /// </summary>
    public static string Name => Path.GetFileNameWithoutExtension(Shell().Executable);

    /// <summary>What the shell this pane starts speaks. Read off the same choice the spawn makes,
    /// so a command written into the pane can never be quoted for a shell that is not there.</summary>
    public static ShellFamily Family => Shell().Family;

    /// <summary>
    /// The spawn, told what kind of terminal it is talking to and roughly what colour it is.
    /// </summary>
    /// <param name="background">
    /// What the pane's default background is being drawn in, which decides <c>COLORFGBG</c>.
    /// </param>
    public static PtySessionOptions For(string workingDirectory, PtySize size, TerminalRgb background)
    {
        var (executable, arguments, _) = Shell();

        return new PtySessionOptions
        {
            Executable = OperatingSystem.IsWindows() ? executable : Acquirer,
            Arguments = OperatingSystem.IsWindows()
                ? arguments
                : ["-c", AcquireAndExec, executable, .. arguments],
            WorkingDirectory = workingDirectory,
            Size = size,
            Environment = new Dictionary<string, string?>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
                ["COLORFGBG"] = ColorFgBg(background),
            },
        };
    }

    /// <summary>
    /// The rxvt-era light/dark hint: two palette indices, foreground and background.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the programs that never ask — a shell prompt framework, or a <c>vim</c> that starts
    /// before its OSC 11 reply lands — and the only signal they get. Almost nothing consumes the
    /// numbers themselves; what is read is whether the background index is 0..6 or 7..15, so the
    /// two ends of the range are all that is worth reporting.
    /// </para>
    /// <para>
    /// Derived from the colour rather than from which theme is selected, so a third theme, or a
    /// light background on a dark theme, cannot make this disagree with the pane. It is fixed at
    /// the spawn and a later theme switch does not revise it — an environment variable is copied
    /// into the child at birth, and the query is what answers honestly after that.
    /// </para>
    /// </remarks>
    static string ColorFgBg(TerminalRgb background) => IsLight(background) ? "0;15" : "15;0";

    // Rec. 601 luma, which is the weighting these two-tone decisions are conventionally made on and
    // close enough for a question whose whole answer space is "light" or "dark".
    static bool IsLight(TerminalRgb c) =>
        (299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000 >= 128;

    static (string Executable, string[] Arguments, ShellFamily Family) Shell() =>
        OperatingSystem.IsWindows() ? WindowsShell() : UnixShell();

    /// <remarks>
    /// Windows has no login shell to consult, so the choice is a preference order: PowerShell 7 if
    /// the user has it, else the Windows PowerShell every installation carries, else the command
    /// processor. <c>-NoLogo</c> only suppresses the banner; the profile still runs, which is what
    /// makes the user's own PATH and aliases live in the pane.
    /// </remarks>
    static (string, string[], ShellFamily) WindowsShell()
    {
        if (OnPath("pwsh.exe")) return ("pwsh.exe", ["-NoLogo"], ShellFamily.PowerShell);
        if (OnPath("powershell.exe")) return ("powershell.exe", ["-NoLogo"], ShellFamily.PowerShell);
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", [], ShellFamily.CommandProcessor);
    }

    /// <remarks>
    /// <c>-l</c> for the same reason <c>GitProcessRunner</c> shells out through a login shell: the
    /// rc files are where nvm, asdf and every hand-edited PATH live, and a shell started without
    /// them cannot find the tools the user expects to type the name of.
    /// </remarks>
    static (string, string[], ShellFamily) UnixShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        return (string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell, ["-l"], ShellFamily.Posix);
    }

    static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim('"'), executable))) return true;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not a reason to fall back to cmd.
            }
        }

        return false;
    }
}

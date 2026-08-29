using GitBench.Pty;

namespace GitBench.Features.Terminal;

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

    public static PtySessionOptions For(string workingDirectory, PtySize size)
    {
        var (executable, arguments) = Shell();

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
            },
        };
    }

    static (string Executable, string[] Arguments) Shell() =>
        OperatingSystem.IsWindows() ? WindowsShell() : UnixShell();

    /// <remarks>
    /// Windows has no login shell to consult, so the choice is a preference order: PowerShell 7 if
    /// the user has it, else the Windows PowerShell every installation carries, else the command
    /// processor. <c>-NoLogo</c> only suppresses the banner; the profile still runs, which is what
    /// makes the user's own PATH and aliases live in the pane.
    /// </remarks>
    static (string, string[]) WindowsShell()
    {
        if (OnPath("pwsh.exe")) return ("pwsh.exe", ["-NoLogo"]);
        if (OnPath("powershell.exe")) return ("powershell.exe", ["-NoLogo"]);
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", []);
    }

    /// <remarks>
    /// <c>-l</c> for the same reason <c>GitProcessRunner</c> shells out through a login shell: the
    /// rc files are where nvm, asdf and every hand-edited PATH live, and a shell started without
    /// them cannot find the tools the user expects to type the name of.
    /// </remarks>
    static (string, string[]) UnixShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        return (string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell, ["-l"]);
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

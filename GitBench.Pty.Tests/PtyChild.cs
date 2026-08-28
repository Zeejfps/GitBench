namespace GitBench.Pty.Tests;

/// <summary>
/// The children these tests spawn. Only programs that ship with Windows, addressed by bare name so
/// that PATH resolution is exercised too.
/// </summary>
static class PtyChild
{
    /// <summary>How long a test waits on a real process before calling it a failure.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static IPtySession Start(PtySessionOptions options) => new PtySessionFactory().Start(options);

    public static PtySessionOptions Cmd(string workingDirectory, params string[] arguments) => new()
    {
        Executable = "cmd.exe",
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
    };

    public static PtySessionOptions PowerShell(string workingDirectory, string command, PtySize? size = null) => new()
    {
        Executable = "powershell.exe",
        Arguments = ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", command],
        WorkingDirectory = workingDirectory,
        Size = size ?? PtySize.Default,
    };

    public static PtySessionOptions PowerShellScript(string workingDirectory, string script, params string[] arguments) => new()
    {
        Executable = "powershell.exe",
        Arguments = new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }
            .Concat(arguments)
            .ToArray(),
        WorkingDirectory = workingDirectory,
    };
}

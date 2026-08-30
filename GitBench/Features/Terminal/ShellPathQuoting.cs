namespace GitBench.Features.Terminal;

/// <summary>
/// Quotes a path for a command typed into a running shell.
/// </summary>
/// <remarks>
/// <para>
/// The first place in this codebase that needs it. Everything else that runs a program builds an
/// argument vector through <c>ProcessStartInfo.ArgumentList</c> and never composes a command line at
/// all; a pty has no argument vector — there is only the line the user would have typed — so the
/// quoting is ours, per shell, and getting it wrong runs whatever a directory name says.
/// </para>
/// <para>
/// The command processor is the one that cannot be made safe: it expands <c>%VAR%</c> inside double
/// quotes and has no escape for a quote character, so a path carrying either is refused rather than
/// half-quoted. A refusal is a menu item that does nothing; a bad quote is arbitrary execution.
/// </para>
/// </remarks>
internal static class ShellPathQuoting
{
    /// <summary>The line to type to change directory, or null when the path cannot be expressed
    /// safely in that shell.</summary>
    public static string? ChangeDirectoryCommand(string absolutePath, ShellFamily family)
    {
        if (string.IsNullOrEmpty(absolutePath)) return null;
        foreach (var c in absolutePath)
            if (char.IsControl(c)) return null;

        return family switch
        {
            ShellFamily.Posix => $"cd -- {QuotePosix(absolutePath)}",
            ShellFamily.PowerShell => $"Set-Location -LiteralPath {QuotePowerShell(absolutePath)}",
            ShellFamily.CommandProcessor => QuoteCommandProcessor(absolutePath) is { } quoted
                ? $"cd /d {quoted}"
                : null,
            _ => null,
        };
    }

    /// <summary>Single quotes take everything literally; the only thing that ends them is a quote,
    /// so each one leaves the string, contributes an escaped quote, and re-enters it.</summary>
    public static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>A single-quoted PowerShell string is literal — no variable or subexpression
    /// expansion — and a quote inside it is written twice.</summary>
    public static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    private static string? QuoteCommandProcessor(string value) =>
        value.Contains('"') || value.Contains('%') || value.Contains('!') ? null : $"\"{value}\"";
}

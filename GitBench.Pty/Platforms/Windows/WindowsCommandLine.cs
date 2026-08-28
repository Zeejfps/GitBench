using System.Text;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// Encodes an executable and its argv as the single command-line string CreateProcessW takes.
/// </summary>
/// <remarks>
/// The encoding is the inverse of <c>CommandLineToArgvW</c>, which is the parser the CRT startup
/// code runs to rebuild argv in the child. The executable is always quoted and its backslashes are
/// left alone, because the parser treats the first token specially: quoting ends at the next quote
/// and backslashes are never escapes there. Arguments follow the ordinary rule, where a run of
/// backslashes only doubles when a quote is what comes after it.
/// </remarks>
internal static class WindowsCommandLine
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        if (executable.Length == 0)
            throw new ArgumentException("An executable is required.", nameof(executable));

        if (executable.Contains('"'))
            throw new ArgumentException(
                $"An executable cannot contain a double quote: {executable}", nameof(executable));

        if (executable.Contains('\0'))
            throw new ArgumentException(
                "An executable cannot contain a null character.", nameof(executable));

        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (argument is null)
                throw new ArgumentException($"Argument {i} is null.", nameof(arguments));

            if (argument.Contains('\0'))
                throw new ArgumentException(
                    $"Argument {i} contains a null character, which no command line can carry.",
                    nameof(arguments));

            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && !NeedsQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            else
                builder.Append('\\', backslashes).Append(argument[i]);
        }

        builder.Append('"');
    }

    static bool NeedsQuoting(string argument)
    {
        foreach (var c in argument)
        {
            if (c == '"' || char.IsWhiteSpace(c))
                return true;
        }

        return false;
    }
}

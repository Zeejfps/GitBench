using System.Text;

namespace GitBench.Features.AgentConnections;

/// <summary>Why a preset's arguments can't be passed to its agent.</summary>
internal abstract record AgentArgumentsProblem
{
    public sealed record UnclosedQuote : AgentArgumentsProblem;

    /// <summary>Claude Code's adapter hands arguments on as long flags, each with at most one
    /// value; <paramref name="Token"/> is neither.</summary>
    public sealed record NotAFlag(string Token) : AgentArgumentsProblem;
}

internal abstract record AgentArgumentsParse
{
    public sealed record Ok(IReadOnlyList<string> Arguments) : AgentArgumentsParse;

    public sealed record Invalid(AgentArgumentsProblem Problem) : AgentArgumentsParse;
}

/// <summary>A long flag as Claude Code's adapter passes it: <c>--name</c>, or <c>--name value</c>.</summary>
internal sealed record ClaudeFlag(string Name, string? Value);

/// <summary>
/// A preset's extra arguments between the text the user types and the list the agent gets: split
/// on whitespace, with single or double quotes grouping, and no escapes so Windows paths pass
/// through as typed.
/// </summary>
internal static class AgentArguments
{
    public static AgentArgumentsParse Parse(string text, AgentKind kind)
    {
        if (Split(text) is not { } arguments) return new AgentArgumentsParse.Invalid(new AgentArgumentsProblem.UnclosedQuote());
        return Check(arguments, kind) is { } problem
            ? new AgentArgumentsParse.Invalid(problem)
            : new AgentArgumentsParse.Ok(arguments);
    }

    /// <summary>What stops <paramref name="arguments"/> from being passed to a <paramref name="kind"/>
    /// agent, or null.</summary>
    public static AgentArgumentsProblem? Check(IReadOnlyList<string> arguments, AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => ClaudeFlags(arguments, out _),
        AgentKind.Codex or AgentKind.Gemini => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown agent."),
    };

    /// <summary>Reads <paramref name="arguments"/> as Claude Code flags. A flag takes the token after
    /// it as its value unless that token is a flag too; <c>--name=value</c> always does.</summary>
    public static AgentArgumentsProblem? ClaudeFlags(IReadOnlyList<string> arguments, out List<ClaudeFlag> flags)
    {
        flags = new List<ClaudeFlag>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2 || token[2] == '=')
                return new AgentArgumentsProblem.NotAFlag(token);

            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                flags.Add(new ClaudeFlag(token[2..equals], token[(equals + 1)..]));
                continue;
            }

            if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith('-'))
            {
                flags.Add(new ClaudeFlag(token[2..], arguments[i + 1]));
                i++;
                continue;
            }

            flags.Add(new ClaudeFlag(token[2..], null));
        }

        return null;
    }

    /// <summary>The tokens in <paramref name="text"/>, or null for a quote left open.</summary>
    public static List<string>? Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!inToken) continue;
                tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote is not null) return null;
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The text that <see cref="Split"/> reads back as <paramref name="arguments"/>.</summary>
    public static string Format(IReadOnlyList<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string token)
    {
        if (token.Length > 0 && !token.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')) return token;
        if (!token.Contains('"')) return $"\"{token}\"";
        if (!token.Contains('\'')) return $"'{token}'";
        return string.Join("'\"'", token.Split('"').Select(part => $"\"{part}\""));
    }
}

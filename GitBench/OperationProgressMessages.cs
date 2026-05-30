using System.Text.RegularExpressions;

namespace GitGui;

public readonly record struct OperationStartedMessage(Guid OpId, string Label, string Icon);

public readonly record struct OperationProgressMessage(
    Guid OpId,
    string? Phase,
    float? Percent,
    string RawLine);

public readonly record struct OperationFinishedMessage(
    Guid OpId,
    bool Success,
    string? ErrorMessage);

internal static class GitProgressParser
{
    private static readonly Regex Pattern = new(
        @"^(?<phase>[A-Za-z][A-Za-z ]+?):\s*(?<pct>\d{1,3})%",
        RegexOptions.Compiled);

    public static (string? Phase, float? Percent) Parse(string line)
    {
        if (string.IsNullOrEmpty(line)) return (null, null);
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("remote: ", StringComparison.Ordinal))
            trimmed = trimmed["remote: ".Length..];
        var match = Pattern.Match(trimmed);
        if (!match.Success) return (null, null);
        if (!int.TryParse(match.Groups["pct"].Value, out var pct)) return (null, null);
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        return (match.Groups["phase"].Value.Trim(), pct / 100f);
    }
}

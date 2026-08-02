using System.Text;
using System.Text.Json;

namespace GitBench.Features.Assistant;

/// <summary>
/// The arguments of one tool call as the few words that fit on its transcript line.
/// </summary>
/// <remarks>
/// "Used read_file" leaves the reader wondering which file, which is the one thing they wanted to
/// know; "Used read_file · src/Git/GitService.cs" answers it in the space already on screen. What
/// makes that affordable is that this is a glance, not a record: values carry the line, keys appear
/// only where a bare number would be a riddle, and the whole thing is clipped rather than wrapped,
/// because a tool line that grows to three rows costs the answer below it more than the detail is
/// worth. The full arguments of a write are still shown in full on its approval card.
///
/// Deliberately generic. A per-tool formatter would read slightly better and would then be one more
/// place every new tool has to be registered — and the place everyone forgets.
/// </remarks>
internal static class ToolCallSummary
{
    private const int MaxLength = 64;
    private const int MaxValue = 44;
    private const int MaxItems = 3;

    public static string? Describe(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        var summary = new StringBuilder();
        foreach (var property in args.EnumerateObject())
        {
            var part = Format(property);
            if (part.Length == 0) continue;

            if (summary.Length > 0) summary.Append(' ');
            summary.Append(part);
            if (summary.Length >= MaxLength) break;
        }

        return summary.Length == 0 ? null : Clip(summary.ToString(), MaxLength);
    }

    // A string is nearly always the thing being addressed — a path, a sha, a pattern — so it stands
    // on its own. A number rarely is, so it keeps its name.
    private static string Format(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.String => Clip(Flatten(property.Value.GetString()), MaxValue),
        JsonValueKind.Number => property.Name + "=" + property.Value.ToString(),
        JsonValueKind.True => property.Name,
        JsonValueKind.False => "no " + property.Name,
        JsonValueKind.Array => FormatArray(property.Value),
        _ => string.Empty,
    };

    private static string FormatArray(JsonElement array)
    {
        var shown = new List<string>(MaxItems);
        var total = 0;
        foreach (var item in array.EnumerateArray())
        {
            total++;
            if (shown.Count < MaxItems && item.ValueKind == JsonValueKind.String)
                shown.Add(Clip(Flatten(item.GetString()), MaxValue));
        }

        if (shown.Count == 0) return total == 0 ? string.Empty : total + " items";
        var text = string.Join(", ", shown);
        return total > shown.Count ? text + " +" + (total - shown.Count) : text;
    }

    // Newlines and runs of spaces would otherwise turn a commit message into a line that no longer
    // fits the row it is drawn on.
    private static string Flatten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var flattened = new StringBuilder(value.Length);
        var space = false;
        foreach (var character in value.Trim())
        {
            if (!char.IsWhiteSpace(character))
            {
                flattened.Append(character);
                space = false;
            }
            else if (!space)
            {
                flattened.Append(' ');
                space = true;
            }
        }

        return flattened.ToString();
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
}

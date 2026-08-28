using System.Collections;
using System.Text;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// Layers an environment overlay over an inherited environment and encodes the result as the
/// environment block CreateProcessW takes.
/// </summary>
/// <remarks>
/// Names collate the way Windows compares them, case-insensitively, so an overlay entry replaces an
/// inherited one that differs only in case rather than producing two. The overlay is caller input
/// and is validated; the inherited set is ambient input and entries that cannot be encoded — the
/// per-drive <c>=C:</c> pseudo-variables among them — are dropped instead of throwing.
/// </remarks>
internal static class WindowsEnvironmentBlock
{
    /// <summary>
    /// Builds the block: <c>NAME=VALUE\0</c> per entry, sorted by name, then a closing <c>\0</c>.
    /// An empty environment is the two nulls on their own.
    /// </summary>
    public static char[] Build(
        IEnumerable<KeyValuePair<string, string>> inherited,
        IReadOnlyDictionary<string, string?> overlay)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(overlay);

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in inherited)
        {
            if (name is null || value is null || !IsEncodableName(name) || value.Contains('\0'))
                continue;

            merged.Remove(name);
            merged.Add(name, value);
        }

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in overlay)
        {
            if (name is null || !IsEncodableName(name))
                throw new ArgumentException(
                    $"'{name}' is not a usable environment variable name.", nameof(overlay));

            if (!applied.Add(name))
                throw new ArgumentException(
                    $"The overlay sets '{name}' twice, under names that differ only by case.",
                    nameof(overlay));

            merged.Remove(name);

            if (value is null)
                continue;

            if (value.Contains('\0'))
                throw new ArgumentException(
                    $"The value of '{name}' contains a null character.", nameof(overlay));

            merged.Add(name, value);
        }

        var names = new List<string>(merged.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        foreach (var name in names)
            builder.Append(name).Append('=').Append(merged[name]).Append('\0');

        if (names.Count == 0)
            builder.Append('\0');

        builder.Append('\0');

        var block = new char[builder.Length];
        builder.CopyTo(0, block, 0, builder.Length);
        return block;
    }

    /// <summary>The environment this process would otherwise pass on to a child.</summary>
    public static IEnumerable<KeyValuePair<string, string>> CaptureInherited()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                yield return new KeyValuePair<string, string>(name, value);
        }
    }

    static bool IsEncodableName(string name) =>
        name.Length > 0 && !name.Contains('=') && !name.Contains('\0');
}

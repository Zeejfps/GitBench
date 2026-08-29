using System.Collections;
using System.Text;

namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// Layers an environment overlay over an inherited environment and encodes the result as the
/// null-terminated UTF-8 strings a POSIX <c>envp</c> carries.
/// </summary>
/// <remarks>
/// <para>
/// Names are byte strings and nothing collates them, so an overlay entry replaces an inherited one
/// only when the two are spelled identically — the one substantive difference from
/// <see cref="Platforms.Windows.WindowsEnvironmentBlock"/>, where <c>Path</c> and <c>PATH</c> are the
/// same variable. The overlay is caller input and is validated; the inherited set is ambient input
/// and entries that cannot be encoded are dropped instead of throwing.
/// </para>
/// <para>
/// Bytes rather than strings, and a separate type rather than a step inside the spawn, so that the
/// encoding is the thing under test: what POSIX carries is the UTF-8, and a merge checked only as
/// strings would leave the encoding proven by nothing but a spawn.
/// </para>
/// <para>
/// It carries no <c>SupportedOSPlatform</c> because it makes no system call — it is string and byte
/// work, and its tests run everywhere the way the Windows block's already do.
/// </para>
/// </remarks>
internal static class UnixEnvironmentBlock
{
    /// <summary>
    /// Builds the block: one <c>NAME=VALUE</c> entry per variable, UTF-8, each with its terminating
    /// null byte, ordered by name so that the result of a given environment is always the same one.
    /// </summary>
    /// <exception cref="ArgumentException">The overlay names or values something POSIX cannot carry.</exception>
    public static byte[][] Build(
        IEnumerable<KeyValuePair<string, string>> inherited,
        IReadOnlyDictionary<string, string?> overlay)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(overlay);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in inherited)
        {
            if (name is null || value is null || !IsEncodableName(name) || value.Contains('\0'))
                continue;

            merged[name] = value;
        }

        foreach (var (name, value) in overlay)
        {
            if (name is null || !IsEncodableName(name))
                throw new ArgumentException(
                    $"'{name}' is not a usable environment variable name.", nameof(overlay));

            if (value is null)
            {
                merged.Remove(name);
                continue;
            }

            if (value.Contains('\0'))
                throw new ArgumentException(
                    $"The value of '{name}' contains a null character.", nameof(overlay));

            merged[name] = value;
        }

        var entries = new List<(byte[] Name, byte[] Encoded)>(merged.Count);

        foreach (var (name, value) in merged)
            entries.Add((Encoding.UTF8.GetBytes(name), Encode(name, value)));

        entries.Sort(static (left, right) => left.Name.AsSpan().SequenceCompareTo(right.Name));

        var block = new byte[entries.Count][];

        for (var i = 0; i < block.Length; i++)
            block[i] = entries[i].Encoded;

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

    static byte[] Encode(string name, string value)
    {
        var text = $"{name}={value}";
        var encoded = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, encoded);
        return encoded;
    }

    static bool IsEncodableName(string name) =>
        name.Length > 0 && !name.Contains('=') && !name.Contains('\0');
}

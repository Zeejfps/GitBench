namespace GitBench.Lsp.Configuration;

/// <summary>
/// A file extension including its leading dot, lower-cased. Written as ".rs" or "rs" in the
/// config; both parse to the same value.
/// </summary>
public readonly record struct FileExtension
{
    static readonly char[] Invalid = ['/', '\\', '*', '?', ':', ' ', '"', '<', '>', '|'];

    readonly string? _value;

    FileExtension(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public static bool TryParse(string? raw, out FileExtension extension)
    {
        extension = default;
        if (raw is null) return false;

        var text = raw.Trim();
        if (text.Length == 0) return false;
        if (text[0] != '.') text = "." + text;
        if (text.Length < 2) return false;
        if (text.IndexOfAny(Invalid) >= 0) return false;
        if (text.AsSpan(1).Contains('.')) return false;

        extension = new FileExtension(text.ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// The extension of a path, or none when the file name has none. A name that is all
    /// extension — ".gitignore" — has none: it is a whole file name, not a kind of file.
    /// </summary>
    public static FileExtension? Of(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var start = path.LastIndexOfAny(['/', '\\']) + 1;
        var name = path.AsSpan(start);
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return null;

        return TryParse(name[dot..].ToString(), out var extension) ? extension : null;
    }

    public override string ToString() => Value;
}

/// <summary>Identifies a repository the app has open.</summary>
public readonly record struct RepositoryId(Guid Value)
{
    public static RepositoryId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>A repository the supervisor can run servers for.</summary>
public sealed record Repository(RepositoryId Id, string RootPath);

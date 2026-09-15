namespace GitBench.Lsp.Documents;

/// <summary>Hover content in the one form the popup renders: markdown.</summary>
public sealed record HoverText(string Markdown)
{
    /// <summary>The hover a server sent, as markdown, or null when it said nothing. Plain text is
    /// fenced rather than passed through: a type signature is full of characters markdown eats.</summary>
    public static HoverText? Of(Hover hover) => hover switch
    {
        Hover.Text(var kind, var value, _) when !string.IsNullOrWhiteSpace(value) =>
            new HoverText(kind == MarkupKind.Markdown ? value : $"```\n{value.Trim()}\n```"),
        _ => null,
    };
}

/// <summary>Where a definition lives. The pane handles the two cases with different machinery —
/// one expands the tree and moves the selection, the other opens a detached preview with no
/// selection at all — so they are different types rather than a path and a flag.</summary>
public abstract record DefinitionTarget
{
    private DefinitionTarget() { }

    public sealed record InRepo(string RelativePath, LspPosition Position) : DefinitionTarget;

    public sealed record OutsideRepo(string AbsolutePath, LspPosition Position) : DefinitionTarget;
}

public enum PathComparison
{
    CaseSensitive,
    CaseInsensitive,
}

/// <summary>Decides whether a path the server named is inside the repository the pane is showing.
/// Most jumps in Rust and Go land in a standard library or a package cache, so this is the common
/// case rather than the exceptional one.</summary>
public sealed class RepoBoundary
{
    private readonly IReadOnlyList<string> _roots;
    private readonly StringComparison _comparison;

    private RepoBoundary(IReadOnlyList<string> roots, StringComparison comparison)
    {
        _roots = roots;
        _comparison = comparison;
    }

    public static RepoBoundary At(string rootPath) =>
        At(rootPath, OperatingSystem.IsLinux() ? PathComparison.CaseSensitive : PathComparison.CaseInsensitive);

    public static RepoBoundary At(string rootPath, PathComparison comparison) =>
        At([rootPath], comparison);

    public static RepoBoundary At(IReadOnlyList<string> rootPaths, PathComparison comparison)
    {
        var lookup = comparison == PathComparison.CaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var roots = new List<string>(rootPaths.Count);
        foreach (var path in rootPaths)
        {
            var root = Normalize(path).TrimEnd('/');
            if (root.Length == 0) continue;
            if (!roots.Any(known => string.Equals(known, root, lookup))) roots.Add(root);
        }

        return new RepoBoundary(roots, lookup);
    }

    public DefinitionTarget Classify(DocumentUri uri, LspPosition position)
    {
        var path = Normalize(uri.LocalPath);
        foreach (var root in _roots)
        {
            var prefix = root + "/";
            if (path.StartsWith(prefix, _comparison))
                return new DefinitionTarget.InRepo(path[prefix.Length..], position);
        }

        return new DefinitionTarget.OutsideRepo(uri.LocalPath, position);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

using System.Text;

using GitBench.Features.Diff;

namespace GitBench.Features.CodeIntel;

/// <summary>
/// One declaration in a file: what it is called, what it contains, and where its own name is
/// written.
/// </summary>
/// <remarks>
/// The lines a fold and a breadcrumb work in stay bare <see cref="int"/>s, deliberately, while
/// <see cref="NameLine"/> and <see cref="NameColumn"/> are typed. The name position is the one
/// value here that crosses out of the app — it is handed to a language server as a position to
/// answer about — and a UTF-8 byte column arriving where a UTF-16 one was expected is answered, not
/// rejected, so nothing anywhere reports the mistake.
/// </remarks>
internal sealed record OutlineNode(
    string Name,
    SymbolKind Kind,
    string? ParameterTypes,
    int StartLine,
    int EndLine,
    int SignatureEndLine,
    FileLine NameLine,
    RawColumn NameColumn,
    IReadOnlyList<OutlineNode> Children);

internal sealed record FileOutline(IReadOnlyList<OutlineNode> Roots)
{
    public OutlineNode? EnclosingAt(int line) => Innermost(Roots, line, null);

    /// <summary>
    /// A declaration's name within its file, qualified by what contains it and by its own parameter
    /// types — <c>App.AuthService.Login(string)</c>. The one name for a declaration that survives an
    /// edit somewhere else in the file, which is what both the tree's open set and the preview's
    /// fold set are keyed by.
    /// </summary>
    public static string PathOf(string? parentPath, OutlineNode node)
    {
        var self = node.ParameterTypes is null ? node.Name : $"{node.Name}({node.ParameterTypes})";
        return parentPath is null ? self : $"{parentPath}.{self}";
    }

    /// <summary>The declarations containing <paramref name="line"/>, outermost first; empty when
    /// it is inside none. The last entry is <see cref="EnclosingAt"/>'s answer.</summary>
    public IReadOnlyList<OutlineNode> EnclosingPathAt(int line)
    {
        var path = new List<OutlineNode>();
        if (Innermost(Roots, line, path) == null) return [];
        path.Reverse();
        return path;
    }

    /// <summary>The declaration containing <paramref name="line"/> as a dotted containment path
    /// (<c>AuthService.Login(string)</c>), or null when it is inside none.</summary>
    public string? DeclarationPathAt(int line) => RenderPath(EnclosingPathAt(line));

    /// <summary>
    /// A containment path as one line of text. Namespaces are elided: within a file they are the
    /// same for every declaration, so they cost width without separating one from another.
    /// </summary>
    public static string? RenderPath(IReadOnlyList<OutlineNode> path)
    {
        var text = new StringBuilder();
        foreach (var node in path)
        {
            if (node.Kind == SymbolKind.Namespace) continue;
            if (text.Length > 0) text.Append('.');
            text.Append(node.Name);
            if (node.ParameterTypes is { } parameters) text.Append('(').Append(parameters).Append(')');
        }

        return text.Length == 0 ? null : text.ToString();
    }

    public IReadOnlyList<OutlineNode> Flatten()
    {
        var flat = new List<OutlineNode>();
        Append(Roots, flat);
        return flat;
    }

    // Children are searched before the node's own span: a file-scoped namespace declares on one
    // line yet contains the rest of the file, so containment is the tree, not the spans. A
    // non-null path collects the chain innermost-first as the recursion unwinds.
    private static OutlineNode? Innermost(IReadOnlyList<OutlineNode> nodes, int line, List<OutlineNode>? path)
    {
        foreach (var node in nodes)
        {
            var match = Innermost(node.Children, line, path)
                ?? (line >= node.StartLine && line <= node.EndLine ? node : null);
            if (match == null) continue;
            path?.Add(node);
            return match;
        }

        return null;
    }

    private static void Append(IReadOnlyList<OutlineNode> nodes, List<OutlineNode> flat)
    {
        foreach (var node in nodes)
        {
            flat.Add(node);
            Append(node.Children, flat);
        }
    }
}

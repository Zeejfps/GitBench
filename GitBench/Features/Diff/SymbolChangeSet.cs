using GitBench.Features.CodeIntel;
using GitBench.Git;

namespace GitBench.Features.Diff;

internal enum SymbolChangeKind { Unchanged, Added, Removed, Modified }

/// <summary>One declaration's fate in a diff, and its changed descendants.</summary>
/// <param name="Path">The declaration's containment chain, namespaces elided — enough to tell two
/// same-named methods on different types apart.</param>
/// <param name="Name">The declaration's own name and parameters, without what contains it. What a
/// summary shows: the file is already named above it, so the chain would spend most of its width
/// repeating the prefix every entry shares.</param>
internal sealed record SymbolChange(
    string Path,
    string Name,
    SymbolKind Symbol,
    SymbolChangeKind Change,
    IReadOnlyList<SymbolChange> Children);

/// <summary>
/// What a diff did to a file's declarations: which were added, removed, and changed in their own
/// right. Answers the question a reviewer actually opens a diff with, which no arrangement of
/// hunks does — a hunk says where the text moved, not what it means.
/// </summary>
/// <remarks>
/// <para>
/// Matching is hierarchical and per level, keyed by <c>(Kind, Name, ParameterTypes)</c>. That is
/// qualified-name equality without ever building a qualified name, and including the parameter
/// types is what stops an added overload reading as a modification of the one beside it.
/// </para>
/// <para>
/// Both sides' outlines and the changed line numbers all come off state the diff already carries,
/// so this costs no git read and no second parse. When only one side has an outline — a diff that
/// only adds lines never fetches the old blob — the summary reports what was <em>touched</em> and
/// claims nothing about what was added or removed, because with one outline those are not things
/// it can tell apart.
/// </para>
/// <para>
/// Namespaces are transparent: their children are lifted to the level above rather than reported
/// under a name every declaration in the file shares.
/// </para>
/// </remarks>
internal static class SymbolChangeSet
{
    public static IReadOnlyList<SymbolChange> Build(DiffResult diff, DiffAnnotations? annotations)
    {
        if (annotations is not { } a) return [];
        if (a.OldSide is null && a.NewSide is null) return [];

        var added = LineNumbers(diff, DiffLineKind.Added);
        var removed = LineNumbers(diff, DiffLineKind.Removed);

        var changes = a is { OldSide: { } before, NewSide: { } after }
            ? Match(before.Roots, after.Roots, parent: null, added, removed)
            : a.NewSide is { } newOnly
                ? Touched(newOnly.Roots, parent: null, added)
                : Touched(a.OldSide!.Roots, parent: null, removed);

        return Prune(changes);
    }

    // One outline, so every declaration is one that still exists: the only question it can answer is
    // whether the change reached inside this declaration or one of the ones it holds.
    private static IReadOnlyList<SymbolChange> Touched(
        IReadOnlyList<OutlineNode> nodes, string? parent, IReadOnlySet<int> changed)
    {
        var result = new List<SymbolChange>();
        foreach (var node in nodes)
        {
            var path = Display(parent, node);
            var children = Touched(node.Children, path, changed);
            if (node.Kind == SymbolKind.Namespace) { result.AddRange(children); continue; }

            result.Add(new SymbolChange(
                path,
                Own(node),
                node.Kind,
                TouchedOwnLines(node, changed) ? SymbolChangeKind.Modified : SymbolChangeKind.Unchanged,
                children));
        }
        return result;
    }

    private static IReadOnlyList<SymbolChange> Match(
        IReadOnlyList<OutlineNode> oldNodes,
        IReadOnlyList<OutlineNode> newNodes,
        string? parent,
        IReadOnlySet<int> added,
        IReadOnlySet<int> removed)
    {
        var oldByKey = ByKey(oldNodes);
        var newByKey = ByKey(newNodes);
        var result = new List<SymbolChange>();

        // New-side order, so the summary reads in the order the file now has.
        foreach (var node in newNodes)
        {
            var path = Display(parent, node);
            if (!oldByKey.TryGetValue(Key(node), out var before))
            {
                result.Add(new SymbolChange(path, Own(node), node.Kind, SymbolChangeKind.Added, []));
                continue;
            }

            var children = Match(before.Children, node.Children, path, added, removed);
            if (node.Kind == SymbolKind.Namespace) { result.AddRange(children); continue; }

            var touched = TouchedOwnLines(node, added) || TouchedOwnLines(before, removed);
            result.Add(new SymbolChange(
                path, Own(node), node.Kind,
                touched ? SymbolChangeKind.Modified : SymbolChangeKind.Unchanged, children));
        }

        // What the new side no longer has, kept at the level it was removed from — its nearest
        // surviving ancestor, which is exactly where this recursion already is.
        foreach (var node in oldNodes)
        {
            if (newByKey.ContainsKey(Key(node))) continue;
            if (node.Kind == SymbolKind.Namespace) continue;
            result.Add(new SymbolChange(
                Display(parent, node), Own(node), node.Kind, SymbolChangeKind.Removed, []));
        }

        return result;
    }

    /// <summary>
    /// Whether a changed line falls inside the declaration but outside every declaration nested in
    /// it. Without the exclusion a changed method would light up its class, its namespace and the
    /// file — a summary saying everything changed, which is a summary saying nothing.
    /// </summary>
    private static bool TouchedOwnLines(OutlineNode node, IReadOnlySet<int> changed)
    {
        if (changed.Count == 0) return false;

        for (var line = node.StartLine; line <= node.EndLine; line++)
        {
            if (!changed.Contains(line)) continue;
            if (!InAnyChild(node, line)) return true;
        }
        return false;
    }

    private static bool InAnyChild(OutlineNode node, int line)
    {
        foreach (var child in node.Children)
            if (line >= child.StartLine && line <= child.EndLine) return true;
        return false;
    }

    // Anything that changed, plus the unchanged ancestors standing between a change and the root:
    // "Login moved" is only legible if you can still see it was AuthService's.
    private static IReadOnlyList<SymbolChange> Prune(IReadOnlyList<SymbolChange> changes)
    {
        var kept = new List<SymbolChange>();
        foreach (var change in changes)
        {
            var children = Prune(change.Children);
            if (change.Change == SymbolChangeKind.Unchanged && children.Count == 0) continue;
            kept.Add(change with { Children = children });
        }
        return kept;
    }

    private static Dictionary<(SymbolKind, string, string?), OutlineNode> ByKey(IReadOnlyList<OutlineNode> nodes)
    {
        var map = new Dictionary<(SymbolKind, string, string?), OutlineNode>();
        foreach (var node in nodes) map.TryAdd(Key(node), node);
        return map;
    }

    private static (SymbolKind, string, string?) Key(OutlineNode node) =>
        (node.Kind, node.Name, node.ParameterTypes);

    // Namespaces are elided the way they are in a hunk header: the same one for every declaration
    // in the file separates nothing. A namespace therefore contributes no segment, and the children
    // it holds are reported at the level above it.
    private static string Display(string? parent, OutlineNode node)
    {
        if (node.Kind == SymbolKind.Namespace) return parent ?? string.Empty;
        var self = Own(node);
        return string.IsNullOrEmpty(parent) ? self : $"{parent}.{self}";
    }

    private static string Own(OutlineNode node) =>
        node.ParameterTypes is null ? node.Name : $"{node.Name}({node.ParameterTypes})";

    private static IReadOnlySet<int> LineNumbers(DiffResult diff, DiffLineKind kind)
    {
        var lines = new HashSet<int>();
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != kind) continue;
                var number = kind == DiffLineKind.Added ? line.NewLineNumber : line.OldLineNumber;
                if (number is { } n) lines.Add(n);
            }
        }
        return lines;
    }
}

using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;

namespace GitBench.Features.Pairing;

/// <summary>
/// Finds where a stop's named declaration is in a file's current text: through the outline where
/// the language has one, and by the name as a whole word where it doesn't or the outline misses it.
/// Never by line number, so a stop survives whatever the user typed since the agent last looked.
/// </summary>
internal static class StopResolver
{
    private const int KnownLimit = 40;

    /// <param name="text">The file's text, or null when there is no such file yet.</param>
    public static StopPlacement Resolve(string absolutePath, string? text, FileOutline? outline, StopTarget target)
    {
        if (text is null) return new StopPlacement.Placed(new StopLocation.NewFile(absolutePath));
        var lines = text.Split('\n');

        // A declaration still to be written is often already called in the file: with an outline
        // to go by and a place to put it, a bare mention of its name is not where it is.
        var byText = outline is null || target.After is not { Length: > 0 };
        if (Find(target.Symbol, lines, outline, byText) is { } found)
            return new StopPlacement.Placed(new StopLocation.OnSymbol(absolutePath, found.At, LineAt(lines, found.At.Line.Value)));

        if (target.After is not { Length: > 0 } after)
            return new StopPlacement.Missed(new StopMiss.NoSuchSymbol(target.Path, target.Symbol, Known(outline)));

        if (Find(after, lines, outline, byText: true) is not { } anchor)
            return new StopPlacement.Missed(new StopMiss.NoSuchAfter(target.Path, after, Known(outline)));

        // The end of the declaration it follows: Enter from there is where the new one starts.
        var line = Math.Clamp(anchor.EndLine, 1, lines.Length);
        return new StopPlacement.Placed(new StopLocation.Insertion(absolutePath, TextPosition.At(line, LineAt(lines, line).Length), after));
    }

    private readonly record struct Found(TextPosition At, int EndLine);

    private static Found? Find(string symbol, string[] lines, FileOutline? outline, bool byText)
    {
        var wanted = Normalize(symbol);
        if (wanted.Length == 0) return null;

        if (outline is not null)
        {
            var named = Declarations(outline);
            foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                foreach (var (path, node, parentEnd) in named)
                {
                    if (!Matches(path, wanted, comparison)) continue;
                    return new Found(new TextPosition(node.NameLine, node.NameColumn), LastLineOf(node, parentEnd, lines));
                }
            }
        }

        if (!byText) return null;
        var name = LastSegment(wanted);
        for (var i = 0; i < lines.Length; i++)
        {
            var column = WholeWord(lines[i], name);
            if (column < 0) continue;
            return new Found(TextPosition.At(i + 1, column), i + 1);
        }

        return null;
    }

    // Each declaration under its containment path without namespaces or parameter lists —
    // Class.Method — which is how a model names a symbol — with the last line of what contains it.
    private static List<(string Path, OutlineNode Node, int ParentEnd)> Declarations(FileOutline outline)
    {
        var named = new List<(string, OutlineNode, int)>();
        Walk(outline.Roots, null, int.MaxValue, named);
        return named;
    }

    private static void Walk(IReadOnlyList<OutlineNode> nodes, string? parent, int parentEnd, List<(string, OutlineNode, int)> named)
    {
        foreach (var node in nodes)
        {
            var path = node.Kind == SymbolKind.Namespace ? parent : parent is null ? node.Name : $"{parent}.{node.Name}";
            if (path is not null && node.Kind != SymbolKind.Namespace) named.Add((path, node, parentEnd));
            Walk(node.Children, path, node.Kind == SymbolKind.Namespace ? parentEnd : node.EndLine, named);
        }
    }

    // The outline's last line counts a declaration that ends with its line break as reaching the
    // next line, which for the last member of a type is the type's own closing line. A member can't
    // end on its container's last line unless it starts there; nor on blank lines.
    private static int LastLineOf(OutlineNode node, int parentEnd, string[] lines)
    {
        var end = node.EndLine;
        if (end == parentEnd && end > node.NameLine.Value) end--;
        while (end > node.NameLine.Value && LineAt(lines, end).Trim().Length == 0) end--;
        return end;
    }

    private static bool Matches(string path, string wanted, StringComparison comparison) =>
        string.Equals(path, wanted, comparison)
        || (path.Length > wanted.Length
            && path.EndsWith(wanted, comparison)
            && path[path.Length - wanted.Length - 1] == '.');

    // Parameter lists and generic arguments go: they are how a model decorates a name, and the
    // outline's own parameter text rarely spells them the same way.
    private static string Normalize(string symbol)
    {
        var trimmed = symbol.Trim();
        var cut = trimmed.IndexOfAny(['(', '<']);
        if (cut >= 0) trimmed = trimmed[..cut];
        return trimmed.Replace("::", ".", StringComparison.Ordinal).Trim('.', ' ');
    }

    private static string LastSegment(string symbol)
    {
        var dot = symbol.LastIndexOf('.');
        return dot >= 0 ? symbol[(dot + 1)..] : symbol;
    }

    private static int WholeWord(string line, string word)
    {
        var from = 0;
        while (from <= line.Length - word.Length)
        {
            var at = line.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0) return -1;
            var end = at + word.Length;
            if ((at == 0 || !IsWordChar(line[at - 1])) && (end == line.Length || !IsWordChar(line[end]))) return at;
            from = at + 1;
        }

        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string LineAt(string[] lines, int line) =>
        line >= 1 && line <= lines.Length ? lines[line - 1].TrimEnd('\r') : string.Empty;

    private static IReadOnlyList<string> Known(FileOutline? outline)
    {
        if (outline is null) return [];
        var names = new List<string>();
        foreach (var (path, _, _) in Declarations(outline))
        {
            if (names.Count == KnownLimit) break;
            names.Add(path);
        }

        return names;
    }
}

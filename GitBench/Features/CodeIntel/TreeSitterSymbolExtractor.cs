using System.Text;

using GitBench.Features.Diff;

using TreeSitter;

namespace GitBench.Features.CodeIntel;

internal sealed class TreeSitterSymbolExtractor : ISymbolExtractor
{
    private readonly TreeSitterGrammars _grammars;
    private readonly Action<string>? _log;
    private int _parseFailureLogged;

    public TreeSitterSymbolExtractor(TreeSitterGrammars grammars, Action<string>? log = null)
    {
        _grammars = grammars;
        _log = log;
        Availability = grammars.OutlinesAny
            ? CodeIntelAvailability.Ready.Instance
            : new CodeIntelAvailability.Unavailable(grammars.OutlineFailure ?? "No language loaded.");
    }

    public CodeIntelAvailability Availability { get; }

    internal bool Supports(CodeLanguage language) => _grammars.Get(language)?.Outline is not null;

    public FileOutline? Extract(string text, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_grammars.Get(language) is not { Outline: { } outline } grammar) return null;
        if (ParseText.Of(text) is not { } input) return null;

        try
        {
            return grammar.Pool.Use(
                (outline, folds: grammar.Folds, input),
                static (session, s) => Walk(session, s.outline, s.folds, s.input.Normalized, s.input.Utf8));
        }
        catch (Exception error)
        {
            LogOnce($"Code intelligence failed to parse a {language} file: {error}");
            return null;
        }
    }

    /// <summary>What a file declares, read off a tree already parsed for it — the incremental path,
    /// where the tree is maintained across edits rather than built per call.</summary>
    /// <param name="normalized">The file with its line endings already normalized — the text
    /// <paramref name="utf8"/> encodes.</param>
    internal FileOutline? Extract(CodeLanguage language, string normalized, byte[] utf8, SyntaxTree root)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(root);

        if (_grammars.Get(language) is not { Outline: { } outline } grammar) return null;
        if (utf8.Length > ParseText.MaxFileBytes) return null;

        try
        {
            return grammar.Pool.Use(
                (outline, folds: grammar.Folds, normalized, byteCount: utf8.Length, root),
                static (session, s) => WalkTree(session, s.outline, s.folds, s.normalized, s.byteCount, s.root.RootNode));
        }
        catch (Exception error)
        {
            LogOnce($"Code intelligence failed to parse a {language} file: {error}");
            return null;
        }
    }

    private void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _parseFailureLogged, 1) == 0) _log?.Invoke(message);
    }

    private static FileOutline? Walk(ParseSession session, OutlineQuery compiled, FoldQuery? folds, string text, byte[] utf8)
    {
        using var tree = session.Parser.Parse(utf8);
        return WalkTree(session, compiled, folds, text, utf8.Length, tree.RootNode);
    }

    private static FileOutline? WalkTree(
        ParseSession session, OutlineQuery compiled, FoldQuery? folds, string text, int byteCount, Node root)
    {
        var roots = Declarations(session, compiled, text, byteCount, root);
        var regions = folds is null ? [] : Regions(session, folds, text, root, roots);
        return roots.Count == 0 && regions.Count == 0 ? null : new FileOutline(roots, regions);
    }

    private static IReadOnlyList<OutlineNode> Declarations(
        ParseSession session, OutlineQuery compiled, string text, int byteCount, Node root)
    {
        var found = new List<Pending>();
        var seen = new HashSet<(uint Start, uint End)>();

        // Deferred to the first surviving match: a file with no declarations in it — a page of
        // prose, a data blob — would otherwise pay to be measured for nothing.
        Utf8ToUtf16Offsets? offsets = null;

        session.Cursor.ForEachMatch(compiled.Query, root, match =>
        {
            if (!compiled.TryReadDefinition(match, out var definition, out var kind)) return;
            if (!match.TryGetNode(compiled.NameCaptureId, out var name)) return;
            if (!seen.Add((definition.StartByte, definition.EndByte))) return;

            var extent = compiled.HasExtentCapture && match.TryGetNode(compiled.ExtentCaptureId, out var extentNode)
                ? extentNode
                : definition;

            var startLine = StartLineOf(definition, compiled.LeadingDecorations);
            var endLine = (int)definition.EndPoint.Row + 1;
            var signatureEndLine = endLine;

            if (compiled.HasBodyCapture && match.TryGetNode(compiled.BodyCaptureId, out var body))
            {
                signatureEndLine = Math.Clamp((int)body.StartPoint.Row + 1, startLine, endLine);
            }

            offsets ??= Utf8ToUtf16Offsets.For(text, byteCount);

            found.Add(new Pending(
                definition.StartByte,
                extent.StartByte,
                extent.EndByte,
                name.Text,
                kind,
                ParameterTypesOf(definition),
                startLine,
                endLine,
                signatureEndLine,
                new FileLine((int)name.StartPoint.Row + 1),
                ColumnOf(name, offsets)));
        });

        if (found.Count == 0) return [];

        found.Sort(static (a, b) =>
        {
            var byStart = a.ExtentStartByte.CompareTo(b.ExtentStartByte);
            if (byStart != 0) return byStart;

            var byEnd = b.ExtentEndByte.CompareTo(a.ExtentEndByte);
            return byEnd != 0 ? byEnd : a.StartByte.CompareTo(b.StartByte);
        });

        var roots = new List<Draft>();
        var open = new Stack<Draft>();

        foreach (var pending in found)
        {
            while (open.Count > 0 && open.Peek().ExtentEndByte <= pending.StartByte)
            {
                open.Pop();
            }

            var draft = new Draft(pending);
            if (open.Count > 0) open.Peek().Children.Add(draft);
            else roots.Add(draft);

            open.Push(draft);
        }

        return Freeze(roots);
    }

    /// <summary>
    /// The folds the fold query finds, less the ones a declaration already provides: one per starting
    /// line, the widest, and none that starts on a declaration's chevron line or folds its body again.
    /// </summary>
    private static IReadOnlyList<FoldRegion> Regions(
        ParseSession session, FoldQuery folds, string text, Node root, IReadOnlyList<OutlineNode> declarations)
    {
        var widest = new SortedDictionary<int, int>();
        session.Cursor.ForEachMatch(folds.Query, root, match =>
        {
            if (!match.TryGetNode(folds.FoldCaptureId, out var node)) return;
            var stop = folds.EndCaptureId is { } endId && match.TryGetNode(endId, out var endNode) ? endNode : node;

            var start = (int)node.StartPoint.Row + 1;
            // A node that swallows its line's newline — a line comment, in some grammars — ends
            // at the start of the next line, which is not a line it covers.
            var endRow = stop.EndPoint.Column == 0 && stop.EndPoint.Row > node.StartPoint.Row
                ? stop.EndPoint.Row - 1
                : stop.EndPoint.Row;
            var end = (int)endRow + 1;
            if (end - start < 2) return;
            if (!widest.TryGetValue(start, out var known) || end > known) widest[start] = end;
        });
        if (widest.Count == 0) return [];

        var lineStarts = LineStarts(text);
        widest = UnderTheirHeaders(widest, text, lineStarts);

        var outline = new FileOutline(declarations);
        var claimedStarts = new HashSet<int>();
        var claimedBodies = new HashSet<(int Line, int End)>();
        foreach (var node in outline.Flatten())
        {
            if (node.SignatureEndLine >= node.EndLine) continue;
            claimedStarts.Add(node.StartLine);
            for (var line = node.StartLine; line <= node.SignatureEndLine; line++) claimedBodies.Add((line, node.EndLine));
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var regions = new List<FoldRegion>();
        foreach (var (start, end) in widest)
        {
            if (claimedStarts.Contains(start) || claimedBodies.Contains((start, end))) continue;

            string? scope = null;
            foreach (var node in outline.EnclosingPathAt(start)) scope = FileOutline.PathOf(scope, node);

            var key = $"{scope}/{LineAt(text, lineStarts, start).Trim()}";
            var occurrence = occurrences.TryGetValue(key, out var seen) ? seen + 1 : 0;
            occurrences[key] = occurrence;
            regions.Add(new FoldRegion($"{key}#{occurrence}", start, end));
        }

        return regions;
    }

    /// <summary>
    /// Moves a fold whose first line is its opening bracket alone up onto the line above — the
    /// <c>if (ready)</c> of a brace on a line of its own — so the chevron sits beside what the block
    /// belongs to and folding it leaves that line on screen rather than a lone brace.
    /// </summary>
    private static SortedDictionary<int, int> UnderTheirHeaders(
        SortedDictionary<int, int> widest, string text, List<int> lineStarts)
    {
        var moved = new SortedDictionary<int, int>();
        foreach (var (start, end) in widest)
        {
            var from = start;
            if (start > 1
                && LineAt(text, lineStarts, start).Trim() is "{" or "[" or "("
                && !widest.ContainsKey(start - 1)
                && !moved.ContainsKey(start - 1)
                && LineAt(text, lineStarts, start - 1).Trim().Length > 0)
            {
                from = start - 1;
            }

            if (!moved.TryGetValue(from, out var known) || end > known) moved[from] = end;
        }
        return moved;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static string LineAt(string text, List<int> lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Count) return string.Empty;
        var from = lineStarts[line - 1];
        var to = line < lineStarts.Count ? lineStarts[line] - 1 : text.Length;
        return text[from..to];
    }

    private static IReadOnlyList<OutlineNode> Freeze(List<Draft> drafts)
    {
        if (drafts.Count == 0) return [];

        var nodes = new OutlineNode[drafts.Count];
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            nodes[i] = new OutlineNode(
                draft.Pending.Name,
                draft.Pending.Kind,
                draft.Pending.ParameterTypes,
                draft.Pending.StartLine,
                draft.Pending.EndLine,
                draft.Pending.SignatureEndLine,
                draft.Pending.NameLine,
                draft.Pending.NameColumn,
                Freeze(draft.Children));
        }

        return nodes;
    }

    /// <summary>
    /// Where a name begins on its own line, as a language server counts columns. Tree-sitter counts
    /// a point's column in bytes, so the answer is the distance between two byte offsets converted
    /// separately rather than the column it reports: the line's own start is
    /// <c>StartByte - StartPoint.Column</c>, and the difference of the two UTF-16 offsets is the
    /// number of code units of this line that precede the name.
    /// </summary>
    private static RawColumn ColumnOf(Node name, Utf8ToUtf16Offsets offsets) =>
        new(offsets.Utf16OffsetOf(name.StartByte)
            - offsets.Utf16OffsetOf(name.StartByte - name.StartPoint.Column));

    private static int StartLineOf(Node definition, IReadOnlyList<string> leadingDecorations)
    {
        if (leadingDecorations.Count == 0) return (int)definition.StartPoint.Row + 1;

        foreach (var child in definition.Children)
        {
            if (leadingDecorations.Contains(child.Type)) continue;
            return (int)child.StartPoint.Row + 1;
        }

        return (int)definition.StartPoint.Row + 1;
    }

    private static string? ParameterTypesOf(Node definition)
    {
        if (definition.ChildByFieldName("parameters") is not { } parameters) return null;

        var listName = parameters.ChildByFieldName("name");
        var builder = new StringBuilder();

        foreach (var child in parameters.NamedChildren)
        {
            if (listName is { } trailing && child.StartByte == trailing.StartByte && child.EndByte == trailing.EndByte)
            {
                continue;
            }

            var type = TypeOf(child);

            if (builder.Length > 0) builder.Append(", ");
            AppendCollapsed(builder, type.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The parameter's declared type, or the whole node where it does not name one — a lambda's
    /// untyped argument, or C#'s <c>params int[] rest</c>, which the grammar spills into the
    /// parameter list as a bare <c>array_type</c> that already <em>is</em> the type.
    /// </summary>
    /// <remarks>
    /// TypeScript writes the type as an annotation node carrying its own colon, so that one case is
    /// unwrapped rather than rendered as <c>": string"</c>.
    /// </remarks>
    private static Node TypeOf(Node parameter)
    {
        // Only a node that also binds a name or a pattern is parameter-shaped. C# spills
        // `params int[] rest` into the parameter list as a bare array_type, which has a type field
        // of its own and yet already *is* the type — reading that field would return `int`.
        var named = parameter.ChildByFieldName("name") ?? parameter.ChildByFieldName("pattern");
        if (named is null || parameter.ChildByFieldName("type") is not { } type) return parameter;

        // TypeScript writes the type as an annotation node carrying its own colon.
        if (type.Type != "type_annotation") return type;

        Node? inner = null;
        foreach (var child in type.NamedChildren) inner = child;
        return inner ?? type;
    }

    private static void AppendCollapsed(StringBuilder builder, string text)
    {
        var start = builder.Length;
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > start;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }
    }

    private readonly record struct Pending(
        uint StartByte,
        uint ExtentStartByte,
        uint ExtentEndByte,
        string Name,
        SymbolKind Kind,
        string? ParameterTypes,
        int StartLine,
        int EndLine,
        int SignatureEndLine,
        FileLine NameLine,
        RawColumn NameColumn);

    private sealed class Draft(Pending pending)
    {
        public Pending Pending { get; } = pending;

        public uint ExtentEndByte { get; } = pending.ExtentEndByte;

        public List<Draft> Children { get; } = [];
    }
}

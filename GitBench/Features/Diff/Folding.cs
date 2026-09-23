using GitBench.Features.CodeIntel;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Where one foldable declaration touches the row stream. A collapsed region is not a row of its
/// own — it is an ordinary line carrying a marker — so a mark says which of the two jobs this row
/// is doing for its fold, and a row can be doing both.
/// </summary>
/// <param name="Id">The declaration's containment chain, which is what a fold is remembered by.</param>
/// <param name="Chevron">This row carries the toggle: the declaration's signature.</param>
/// <param name="Chip">Set where this row ends a collapsed fold: what it shows in place of the
/// lines the fold hides.</param>
internal readonly record struct FoldMark(string Id, bool Collapsed, bool Chevron, FoldChip? Chip);

/// <summary>What a collapsed fold's row shows after its own text, which follows from what the fold
/// hides.</summary>
internal abstract record FoldChip
{
    private FoldChip() { }

    /// <summary>A declaration's body opening on a line of its own, braces and all: the declaration
    /// reads as one line.</summary>
    public sealed record Body : FoldChip
    {
        public static readonly Body Instance = new();
    }

    /// <summary>The pill alone. Either the fold's closing line stays on a row of its own because
    /// another fold starts on it — the <c>} else {</c> of a folded <c>if</c> — or it has no closing
    /// line at all, ending on a line of its own content as a Python block or a Markdown section
    /// does, and that line is hidden with the rest.</summary>
    public sealed record Interior : FoldChip
    {
        public static readonly Interior Instance = new();
    }

    /// <summary>A fold's lines after its first, with its closing line pulled up behind the chip, so
    /// a folded element reads <c>&lt;header&gt;...&lt;/header&gt;</c> and a declaration whose body
    /// opens on its signature's line reads <c>const theme = {...};</c>.</summary>
    /// <param name="Closing">The line pulled up.</param>
    /// <param name="Indent">How many tab-expanded cells of leading space were trimmed off it — what
    /// its syntax spans are shifted by.</param>
    /// <param name="Text">The closing line, tab-expanded, without its indent.</param>
    /// <param name="Spans">Its syntax spans in <paramref name="Text"/>'s own columns; resolved when
    /// the row is built, since only the row builder holds the highlight.</param>
    public sealed record Joined(FileLine Closing, int Indent, string Text, IReadOnlyList<TokenSpan>? Spans) : FoldChip;
}

/// <summary>
/// Which declarations the reader has folded shut in one file. Keyed by containment chain rather
/// than by position, so a re-read of a file edited above a fold leaves it shut instead of springing
/// it open — the reconcile tick runs twice a minute, and folds that survived only an untouched file
/// would be folds that never survived anything.
/// </summary>
/// <remarks>
/// Carries the path it belongs to so a state built for one file can never be applied to the next
/// one to arrive. Lives on the view model, touched only on the UI thread, not persisted.
/// </remarks>
internal sealed record FoldState(string Path, IReadOnlySet<string> Collapsed)
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    public static FoldState Open(string path) => new(path, Nothing);

    public bool IsCollapsed(string id) => Collapsed.Contains(id);

    public FoldState Toggled(string id)
    {
        var next = new HashSet<string>(Collapsed, StringComparer.Ordinal);
        if (!next.Remove(id)) next.Add(id);
        return this with { Collapsed = next };
    }

    public FoldState Expanded(string id)
    {
        if (!Collapsed.Contains(id)) return this;
        var next = new HashSet<string>(Collapsed, StringComparer.Ordinal);
        next.Remove(id);
        return this with { Collapsed = next };
    }
}

/// <summary>What the fold set means for one file's lines: which are hidden, which carry a chevron
/// or a chip, what each collapsed fold swallowed, and where the usages rows go.</summary>
internal sealed class FoldPlan
{
    public static readonly FoldPlan Nothing = new();

    private readonly Dictionary<int, FoldMark> _marks = new();
    private readonly Dictionary<int, string> _swallowed = new();
    private readonly Dictionary<int, DiffRow.Lens> _lenses = new();
    private readonly List<(int From, int To)> _hidden = new();
    private readonly HashSet<int> _foldStarts = new();
    private readonly bool _usageLens;

    private FoldPlan(bool usageLens = false) => _usageLens = usageLens;

    public static FoldPlan Build(
        FileOutline? outline, FoldState? folds, bool usageLens, IReadOnlyList<string> lines)
    {
        if (outline is null) return Nothing;
        if (folds is null && !usageLens) return Nothing;

        var plan = new FoldPlan(usageLens);
        if (folds is not null) plan.CollectFoldStarts(outline);
        plan.Walk(outline.Roots, parentPath: null, folds, lines, insideBody: false);
        if (folds is not null) plan.PlanRegions(outline.Regions, folds, lines);
        plan.Normalize();
        return plan;
    }

    public bool IsEmpty => _marks.Count == 0 && _lenses.Count == 0 && _hidden.Count == 0;

    /// <summary>The declaration whose collapsed body swallowed a line, or null where the line is visible.</summary>
    public string? CollapsedOver(int line)
    {
        foreach (var (from, to) in _hidden)
        {
            if (line < from) return null;
            if (line <= to) return MarkAt(from - 1)?.Id;
        }
        return null;
    }

    public bool IsHidden(int line)
    {
        var low = 0;
        var high = _hidden.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (from, to) = _hidden[mid];
            if (line < from) high = mid - 1;
            else if (line > to) low = mid + 1;
            else return true;
        }
        return false;
    }

    public FoldMark? MarkAt(int line) => _marks.TryGetValue(line, out var mark) ? mark : null;

    public string? SwallowedAt(int line) => _swallowed.TryGetValue(line, out var text) ? text : null;

    /// <summary>The usages row that goes above a line, or null where none does.</summary>
    public DiffRow.Lens? LensAt(int line) => _lenses.TryGetValue(line, out var lens) ? lens : null;

    /// <param name="insideBody">Whether these declarations sit inside code that runs — a method's
    /// body, a function's — where none of them gets a usages row.</param>
    private void Walk(
        IReadOnlyList<OutlineNode> nodes, string? parentPath, FoldState? folds, IReadOnlyList<string> lines,
        bool insideBody)
    {
        foreach (var node in nodes)
        {
            var path = FileOutline.PathOf(parentPath, node);
            var childrenInsideBody = insideBody || RunsCode(node.Kind);

            // StartLine already skips attributes, decorators and annotations, so the lens sits
            // directly above the signature rather than above whatever decorates it.
            if (_usageLens && !insideBody && HasLens(node.Kind))
                _lenses[node.StartLine] = new DiffRow.Lens(
                    new FileLine(node.StartLine),
                    path,
                    IndentOf(lines, node.StartLine),
                    node.NameLine,
                    node.NameColumn);

            // With no fold set the walk is here only to place lenses: nothing marks and nothing
            // hides, so every declaration below is still reached.
            if (folds is null)
            {
                Walk(node.Children, path, folds, lines, childrenInsideBody);
                continue;
            }

            // §4.1 sets SignatureEndLine to EndLine for anything declared without a body, so this
            // one comparison rules out expression-bodied members, interface members, abstract
            // methods, positional records, delegates and enum members alike.
            if (node.SignatureEndLine >= node.EndLine)
            {
                Walk(node.Children, path, folds, lines, childrenInsideBody);
                continue;
            }

            var collapsed = folds.IsCollapsed(path);
            Mark(node.StartLine, path, collapsed, chevron: true, chip: null);
            if (!collapsed)
            {
                Walk(node.Children, path, folds, lines, childrenInsideBody);
                continue;
            }

            // The body's opening brace goes with the body, so the chip lands on the last line
            // of the signature and the declaration collapses onto one row. Never onto the row
            // carrying the chevron's own start, which is what the Max guards: a signature and
            // its brace sometimes share a line.
            var hideFrom = Math.Max(node.StartLine + 1, node.SignatureEndLine);
            var chipLine = hideFrom - 1;
            // Where they share it the brace is already on screen, so a chip standing for the braces
            // too would draw a second one.
            var (last, chip) = Collapse(hideFrom, node.EndLine, opensOnChipLine: node.SignatureEndLine == node.StartLine, lines);
            if (last < hideFrom) continue;

            Mark(chipLine, path, collapsed: true, chevron: false, chip);
            _hidden.Add((hideFrom, last));
            _swallowed[chipLine] = JoinLines(lines, hideFrom, last);
        }
    }

    // Planned after every declaration, so a line a declaration folds from — or hides — is already
    // spoken for. Regions come in source order, which puts an outer one's hidden range in place
    // before any region it contains is reached.
    private void PlanRegions(IReadOnlyList<FoldRegion> regions, FoldState folds, IReadOnlyList<string> lines)
    {
        foreach (var region in regions)
        {
            if (_marks.ContainsKey(region.StartLine) || Covered(region.StartLine)) continue;

            var collapsed = folds.IsCollapsed(region.Id);
            var hideFrom = region.StartLine + 1;
            var (last, chip) = Collapse(hideFrom, region.EndLine, opensOnChipLine: true, lines);
            if (!collapsed || last < hideFrom)
            {
                Mark(region.StartLine, region.Id, collapsed, chevron: true, chip: null);
                continue;
            }

            Mark(region.StartLine, region.Id, collapsed: true, chevron: true, chip);
            _hidden.Add((hideFrom, last));
            _swallowed[region.StartLine] = JoinLines(lines, hideFrom, last);
        }
    }

    private void CollectFoldStarts(FileOutline outline)
    {
        foreach (var node in outline.Flatten())
            if (node.SignatureEndLine < node.EndLine) _foldStarts.Add(node.StartLine);
        foreach (var region in outline.Regions) _foldStarts.Add(region.StartLine);
    }

    /// <summary>How far a collapsed fold hides and what its chip shows, from what its last line is.
    /// A closing bracket comes up behind the chip, unless another fold starts on its line and would
    /// lose its chevron; a closing line that is content, as a Python block's is, goes with the rest.</summary>
    /// <param name="opensOnChipLine">Whether the opening bracket stays on screen. Where it is hidden
    /// with the body — a brace on a line of its own — the chip stands for both braces instead.</param>
    private (int Last, FoldChip Chip) Collapse(int hideFrom, int endLine, bool opensOnChipLine, IReadOnlyList<string> lines)
    {
        var last = Math.Min(endLine, lines.Count);
        if (endLine > lines.Count || !ClosesBracket(lines[endLine - 1])) return (last, FoldChip.Interior.Instance);
        if (_foldStarts.Contains(endLine)) return (endLine - 1, FoldChip.Interior.Instance);
        return opensOnChipLine ? (last, ClosingOf(lines, endLine)) : (last, FoldChip.Body.Instance);
    }

    private static readonly string[] Closers = ["}", ")", "]", "</", "/>", "*/", "-->"];

    private static bool ClosesBracket(string line)
    {
        var text = line.AsSpan().TrimStart();
        foreach (var closer in Closers)
            if (text.StartsWith(closer, StringComparison.Ordinal)) return true;
        return false;
    }

    private static FoldChip.Joined ClosingOf(IReadOnlyList<string> lines, int line)
    {
        var expanded = DiffText.ExpandTabs(lines[line - 1]);
        var indent = 0;
        while (indent < expanded.Length && expanded[indent] == ' ') indent++;
        return new FoldChip.Joined(new FileLine(line), indent, expanded[indent..].TrimEnd(), Spans: null);
    }

    // Linear, because it runs while the hidden ranges are still unsorted: only collapsed folds add
    // one, and there are only ever a handful.
    private bool Covered(int line)
    {
        foreach (var (from, to) in _hidden)
            if (line >= from && line <= to) return true;
        return false;
    }

    // Ordered and disjoint, which is what IsHidden's binary search needs. Ranges that merely touch
    // are left apart: merging them would attribute the second declaration's lines to the first, and
    // CollapsedOver would then open a fold that does not hide the caret.
    private void Normalize()
    {
        if (_hidden.Count <= 1) return;

        _hidden.Sort(static (a, b) => a.From.CompareTo(b.From));
        var kept = 0;
        for (var i = 1; i < _hidden.Count; i++)
        {
            if (_hidden[i].From <= _hidden[kept].To)
                _hidden[kept] = (_hidden[kept].From, Math.Max(_hidden[kept].To, _hidden[i].To));
            else
                _hidden[++kept] = _hidden[i];
        }
        _hidden.RemoveRange(kept + 1, _hidden.Count - kept - 1);
    }

    // What a body of statements belongs to. Inside one, the only declarations are local functions —
    // and, far more often, a statement half typed that the parser's error recovery reads as one,
    // together with the line below it. A row reserved for that would open above the line being
    // typed and close again once it parsed, and its count would be a question about nothing.
    private static bool RunsCode(SymbolKind kind) => kind switch
    {
        SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function
            or SymbolKind.Property or SymbolKind.Event => true,
        SymbolKind.Namespace or SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface
            or SymbolKind.Record or SymbolKind.Enum or SymbolKind.Type or SymbolKind.Field
            or SymbolKind.EnumMember => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled symbol kind."),
    };

    // Namespaces, fields and enum members are left out deliberately: a lens above every field
    // is chrome nobody asked for, and a namespace's usages are not a question about this file.
    private static bool HasLens(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Record
            or SymbolKind.Enum or SymbolKind.Method or SymbolKind.Constructor
            or SymbolKind.Property or SymbolKind.Event or SymbolKind.Function
            or SymbolKind.Type => true,
        SymbolKind.Namespace or SymbolKind.Field or SymbolKind.EnumMember => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled symbol kind."),
    };

    // The declaration's own indent, in the tab-expanded cells the row grid counts, so the lens
    // starts where the signature under it does rather than at the margin.
    private static int IndentOf(IReadOnlyList<string> lines, int line)
    {
        if (line < 1 || line > lines.Count) return 0;

        var cells = 0;
        foreach (var ch in lines[line - 1])
        {
            if (ch == '\t') cells += DiffOptions.TabWidth - cells % DiffOptions.TabWidth;
            else if (ch == ' ') cells++;
            else break;
        }
        return cells;
    }

    // A signature and its opening brace share a row in some styles, so the two marks merge
    // rather than one overwriting the other.
    private void Mark(int line, string path, bool collapsed, bool chevron, FoldChip? chip)
    {
        var existing = _marks.TryGetValue(line, out var m) ? m : new FoldMark(path, collapsed, false, null);
        _marks[line] = existing with
        {
            Id = path,
            Collapsed = collapsed,
            Chevron = existing.Chevron || chevron,
            Chip = existing.Chip ?? chip,
        };
    }

    // Raw, like the visible rows' own raw text: this is only ever re-inflated into a copy.
    private static string JoinLines(IReadOnlyList<string> lines, int from, int to)
    {
        var text = new System.Text.StringBuilder();
        for (var line = from; line <= to; line++)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(lines[line - 1]);
        }
        return text.ToString();
    }
}

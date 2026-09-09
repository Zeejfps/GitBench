using GitBench.Features.CodeIntel;

namespace GitBench.Features.Diff;

/// <summary>
/// Where one foldable declaration touches the row stream. A collapsed region is not a row of its
/// own — it is an ordinary line carrying a marker — so a mark says which of the two jobs this row
/// is doing for its fold, and a row can be doing both.
/// </summary>
/// <param name="Id">The declaration's containment chain, which is what a fold is remembered by.</param>
/// <param name="Chevron">This row carries the toggle: the declaration's signature.</param>
/// <param name="Chip">This row ends a collapsed fold and shows the continuation.</param>
internal readonly record struct FoldMark(string Id, bool Collapsed, bool Chevron, bool Chip);

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
    private readonly bool _usageLens;

    private FoldPlan(bool usageLens = false) => _usageLens = usageLens;

    public static FoldPlan Build(
        FileOutline? outline, FoldState? folds, bool usageLens, IReadOnlyList<string> lines)
    {
        if (outline is null) return Nothing;
        if (folds is null && !usageLens) return Nothing;

        var plan = new FoldPlan(usageLens);
        plan.Walk(outline.Roots, parentPath: null, folds, lines);
        plan.Normalize();
        return plan;
    }

    public bool IsEmpty => _marks.Count == 0 && _lenses.Count == 0 && _hidden.Count == 0;

    /// <summary>The collapsed line ranges, ordered and disjoint.</summary>
    public IReadOnlyList<(int From, int To)> Hidden => _hidden;

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

    private void Walk(
        IReadOnlyList<OutlineNode> nodes, string? parentPath, FoldState? folds, IReadOnlyList<string> lines)
    {
        foreach (var node in nodes)
        {
            var path = FileOutline.PathOf(parentPath, node);

            // StartLine already skips attributes, decorators and annotations, so the lens sits
            // directly above the signature rather than above whatever decorates it.
            if (_usageLens && HasLens(node.Kind))
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
                Walk(node.Children, path, folds, lines);
                continue;
            }

            // §4.1 sets SignatureEndLine to EndLine for anything declared without a body, so this
            // one comparison rules out expression-bodied members, interface members, abstract
            // methods, positional records, delegates and enum members alike.
            if (node.SignatureEndLine >= node.EndLine)
            {
                Walk(node.Children, path, folds, lines);
                continue;
            }

            var collapsed = folds.IsCollapsed(path);
            Mark(node.StartLine, path, collapsed, chevron: true, chip: false);
            if (!collapsed)
            {
                Walk(node.Children, path, folds, lines);
                continue;
            }

            // The body's opening brace goes with the body, so the chip lands on the last line
            // of the signature and the declaration collapses onto one row. Never onto the row
            // carrying the chevron's own start, which is what the Max guards: a signature and
            // its brace sometimes share a line.
            var hideFrom = Math.Max(node.StartLine + 1, node.SignatureEndLine);
            var chipLine = hideFrom - 1;
            var last = Math.Min(node.EndLine, lines.Count);
            if (last < hideFrom) continue;

            Mark(chipLine, path, collapsed: true, chevron: false, chip: true);
            _hidden.Add((hideFrom, last));
            _swallowed[chipLine] = JoinLines(lines, hideFrom, last);
        }
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
    private void Mark(int line, string path, bool collapsed, bool chevron, bool chip)
    {
        var existing = _marks.TryGetValue(line, out var m) ? m : new FoldMark(path, collapsed, false, false);
        _marks[line] = existing with
        {
            Id = path,
            Collapsed = collapsed,
            Chevron = existing.Chevron || chevron,
            Chip = existing.Chip || chip,
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

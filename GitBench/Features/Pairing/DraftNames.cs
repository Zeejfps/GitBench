using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Infrastructure;
using GitBench.Lsp.Documents;
using GitBench.Theming;

namespace GitBench.Features.Pairing;

/// <summary>A name in the agent's code worth asking a language server about: the line of the code
/// it is on, 0-based, and its raw columns.</summary>
internal sealed record DraftNameAt(int Line, RawColumn Start, RawColumn End, string Text);

/// <summary>
/// The names in the agent's code for a stop, and what a language server made of them: declared
/// already, declared by the code itself, or declared nowhere.
/// </summary>
internal static class DraftNames
{
    /// <summary>More than a step's worth; a block past it is asked about in part.</summary>
    public const int MaxNames = 200;

    /// <summary>
    /// The identifiers in the code that the colors say are names — types, functions, variables —
    /// and nothing else. The colors are required: without them a keyword would be asked about,
    /// answered nowhere, and marked as something still to write.
    /// </summary>
    public static IReadOnlyList<DraftNameAt> In(IReadOnlyList<string> code, IReadOnlyList<IReadOnlyList<TokenSpan>> spans)
    {
        var names = new List<DraftNameAt>();
        for (var i = 0; i < code.Count && names.Count < MaxNames; i++)
        {
            var line = DiffLineText.Of(code[i]);
            var raw = line.Raw;
            var lineSpans = i < spans.Count ? spans[i] : [];
            var at = 0;
            while (at < raw.Length && names.Count < MaxNames)
            {
                if (!IsNamePart(raw[at]))
                {
                    at++;
                    continue;
                }

                var start = at;
                while (at < raw.Length && IsNamePart(raw[at])) at++;
                if (char.IsDigit(raw[start])) continue;
                if (!IsName(SlotAt(lineSpans, line.ToExpanded(new RawColumn(start)).Value))) continue;
                names.Add(new DraftNameAt(i, new RawColumn(start), new RawColumn(at), raw[start..at]));
            }
        }

        return names;
    }

    /// <summary>
    /// What each name is, from the server's answers in the same order. A name declared nowhere is
    /// only called missing when <paramref name="settled"/> — a server still loading answers
    /// "nowhere" about everything it has not read yet — and a name it would not answer about is
    /// left out, since that says nothing about the code.
    /// </summary>
    public static IReadOnlyList<DraftName> Classify(
        IReadOnlyList<DraftNameAt> names,
        IReadOnlyList<DraftDefinition> answers,
        DraftSplice splice,
        string absolutePath,
        string repoRoot,
        bool settled)
    {
        var classified = new List<DraftName>(names.Count);
        for (var i = 0; i < names.Count && i < answers.Count; i++)
        {
            var docs = answers[i] is DraftDefinition.Declared { Hover: { } hover } ? hover.Markdown : null;
            DraftNameKind? kind = answers[i] switch
            {
                DraftDefinition.Declared declared => KindOf(declared.Targets[0], splice, absolutePath, repoRoot),
                DraftDefinition.Undeclared => settled ? DraftNameKind.Missing.Instance : null,
                DraftDefinition.Unanswered => null,
                _ => throw new ArgumentOutOfRangeException(nameof(answers), answers[i], "Unknown draft definition."),
            };
            if (kind is null) continue;
            var name = names[i];
            classified.Add(new DraftName(name.Line, name.Start, name.End, name.Text, kind, docs));
        }

        return classified;
    }

    /// <summary>The names declared nowhere, each once, in the order the code uses them.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<DraftName> names) =>
        names.Where(name => name.Kind is DraftNameKind.Missing).Select(name => name.Text).Distinct(StringComparer.Ordinal).ToArray();

    private static DraftNameKind KindOf(DefinitionTarget target, DraftSplice splice, string absolutePath, string repoRoot)
    {
        var (path, line) = target switch
        {
            DefinitionTarget.InRepo inside => (
                PathKey.Normalize(Path.Combine(repoRoot, inside.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                inside.Position.Line.ToOneBased()),
            DefinitionTarget.OutsideRepo outside => (outside.AbsolutePath, outside.Position.Line.ToOneBased()),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown definition target."),
        };
        if (!PathKey.Comparer.Equals(path, PathKey.Normalize(absolutePath)))
            return new DraftNameKind.Existing(path, new FileLine(line));
        return splice.InCode(line)
            ? DraftNameKind.Introduced.Instance
            : new DraftNameKind.Existing(path, new FileLine(splice.FileLineOf(line)));
    }

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static TokenColorSlot SlotAt(IReadOnlyList<TokenSpan> spans, int column)
    {
        foreach (var span in spans)
            if (span.Start <= column && column < span.Start + span.Length) return span.Slot;
        return TokenColorSlot.Default;
    }

    // Only what a grammar called a name. Uncolored words are prose in markup more often than code,
    // and constants are mostly the language's own — true, null, nil — which nothing declares.
    private static bool IsName(TokenColorSlot slot) => slot switch
    {
        TokenColorSlot.Type or TokenColorSlot.Function or TokenColorSlot.Variable or TokenColorSlot.Struct
            or TokenColorSlot.Interface or TokenColorSlot.Enum or TokenColorSlot.TypeParameter => true,
        TokenColorSlot.Default or TokenColorSlot.Constant or TokenColorSlot.Keyword or TokenColorSlot.String
            or TokenColorSlot.Comment or TokenColorSlot.Number or TokenColorSlot.Operator or TokenColorSlot.Punctuation
            or TokenColorSlot.Heading or TokenColorSlot.Emphasis or TokenColorSlot.Link or TokenColorSlot.Code
            or TokenColorSlot.Quote => false,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown token color slot."),
    };
}

using GitBench.Infrastructure;
using GitBench.Lsp;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>One recolored name on a line, in the line's raw UTF-16 columns.</summary>
internal readonly record struct SemanticColor(RawColumn Start, RawColumn End, TokenColorSlot Slot);

/// <summary>
/// What a language server says the types in a file are, laid over the parser's colors. Held beside
/// the rows rather than folded into them, like diagnostics: it arrives seconds after the file and
/// again after every wave of analysis, while the same rows stay on screen.
/// </summary>
/// <remarks>
/// Addressed by a line's text rather than its number. The answer is about the text the server was
/// sent, and the reader may have typed since: keyed by text, a line that moved keeps its colors, a
/// line that was edited drops back to the parser's until the next answer, and nothing lands on a
/// line it was not computed for. Two identical lines classify identically in all but contrived code,
/// so the first one's colors stand for both.
/// </remarks>
internal sealed class SemanticColorOverlay
{
    public static readonly SemanticColorOverlay Empty = new(string.Empty, new Dictionary<string, SemanticColor[]>());

    private readonly IReadOnlyDictionary<string, SemanticColor[]> _byText;

    private SemanticColorOverlay(string path, IReadOnlyDictionary<string, SemanticColor[]> byText)
    {
        Path = path;
        _byText = byText;
    }

    public string Path { get; }

    public bool IsEmpty => _byText.Count == 0;

    /// <summary>Keeps only the tokens that recolor something, and drops any that do not fit the line
    /// the server said they were on — a server answering about text other than what it was sent.</summary>
    public static SemanticColorOverlay Of(string path, string text, SemanticTokens tokens)
    {
        var lines = TextLines.Split(text);
        var byLine = new Dictionary<int, List<SemanticColor>>();
        foreach (var token in tokens.Tokens)
        {
            if (SemanticTokenMap.Map(token.Type) is not { } slot) continue;
            var line = token.Line.Value;
            if (line >= lines.Count) continue;

            var start = token.Start.Value;
            var end = start + token.Length;
            if (token.Length <= 0 || end > lines[line].Length) continue;

            if (!byLine.TryGetValue(line, out var colors)) byLine[line] = colors = [];
            colors.Add(new SemanticColor(new RawColumn(start), new RawColumn(end), slot));
        }

        var byText = new Dictionary<string, SemanticColor[]>(StringComparer.Ordinal);
        foreach (var (line, colors) in byLine) byText.TryAdd(lines[line], colors.ToArray());
        return new SemanticColorOverlay(path, byText);
    }

    /// <summary>The line's parser colors with the server's laid over them, or the parser's alone
    /// when the server said nothing about a line that reads like this one.</summary>
    public IReadOnlyList<TokenSpan>? Recolor(DiffLineText text, IReadOnlyList<TokenSpan>? parsed)
    {
        if (!_byText.TryGetValue(text.Raw, out var colors)) return parsed;

        var over = new TokenSpan[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var start = text.ToExpanded(colors[i].Start).Value;
            var end = text.ToExpanded(colors[i].End).Value;
            over[i] = new TokenSpan(start, end - start, colors[i].Slot);
        }

        return TokenSpans.Overlay(parsed, over);
    }
}

internal static class TokenSpans
{
    /// <summary>
    /// <paramref name="under"/> with <paramref name="over"/> painted on top, as the sorted,
    /// non-overlapping runs the painter expects. A keyword is never repainted: <c>int</c> is a
    /// struct to a compiler, and to a reader it is a keyword.
    /// </summary>
    public static IReadOnlyList<TokenSpan> Overlay(IReadOnlyList<TokenSpan>? under, IReadOnlyList<TokenSpan> over)
    {
        var width = 0;
        if (under is not null)
            foreach (var span in under) width = Math.Max(width, span.Start + span.Length);
        foreach (var span in over) width = Math.Max(width, span.Start + span.Length);

        var slots = new TokenColorSlot[width];
        if (under is not null)
            foreach (var span in under) Fill(slots, span, keepKeywords: false);
        foreach (var span in over) Fill(slots, span, keepKeywords: true);

        var runs = new List<TokenSpan>();
        var runStart = 0;
        for (var column = 1; column <= width; column++)
        {
            if (column < width && slots[column] == slots[runStart]) continue;
            if (slots[runStart] != TokenColorSlot.Default)
                runs.Add(new TokenSpan(runStart, column - runStart, slots[runStart]));
            runStart = column;
        }

        return runs;
    }

    private static void Fill(TokenColorSlot[] slots, TokenSpan span, bool keepKeywords)
    {
        var end = Math.Min(slots.Length, span.Start + span.Length);
        for (var column = Math.Max(0, span.Start); column < end; column++)
        {
            if (keepKeywords && slots[column] == TokenColorSlot.Keyword) continue;
            slots[column] = span.Slot;
        }
    }
}

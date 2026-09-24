using GitBench.Features.Diff;
using GitBench.Theming;

namespace GitBench.Features.Pairing;

/// <summary>
/// The syntax colors of the agent's code for a stop, taken from the file as it would read with the
/// code in it, so a line of markup or the inside of a string is colored as it will be once taken.
/// Works off the file as the stop found it; any language the app colors.
/// </summary>
internal static class DraftColors
{
    /// <summary>One list of spans per line of <paramref name="code"/>, or null when the file's
    /// language is not colored.</summary>
    public static IReadOnlyList<IReadOnlyList<TokenSpan>>? Of(
        string path, IReadOnlyList<string> file, DraftPlace place, IReadOnlyList<string> code, ISyntaxHighlighter highlighter)
    {
        if (code.Count == 0) return null;
        var (keepAbove, resumeAt) = place switch
        {
            DraftPlace.Replace replace => (replace.Lines.From - 1, replace.Lines.To),
            DraftPlace.InsertAfter insert => (insert.Line.Value, insert.Line.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(place), place, "Unknown draft place."),
        };
        keepAbove = Math.Clamp(keepAbove, 0, file.Count);
        resumeAt = Math.Clamp(resumeAt, keepAbove, file.Count);

        var text = string.Join("\n", file.Take(keepAbove).Concat(code).Concat(file.Skip(resumeAt)));
        if (highlighter.Highlight(text, FileLanguage.Detect(path)) is not { } lines) return null;

        var spans = new IReadOnlyList<TokenSpan>[code.Count];
        for (var i = 0; i < code.Count; i++)
            spans[i] = keepAbove + i < lines.Count ? lines[keepAbove + i] : [];
        return spans;
    }
}

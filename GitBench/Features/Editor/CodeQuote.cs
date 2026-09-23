using System.Text;
using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>Code the user selected and sent to the agent, with where it came from.</summary>
internal abstract record CodeQuote(string Text)
{
    /// <summary>At most this many lines go with a quote from a file.</summary>
    public const int MaxLines = 1000;

    /// <summary>Selected in a file: its first and last line, both 1-based and inclusive.</summary>
    public sealed record InFile(string AbsolutePath, FileLine Start, FileLine End, string Text) : CodeQuote(Text)
    {
        /// <summary>The file's URI with the lines as a fragment, the way editors link a range.</summary>
        public Uri Uri => new($"{new Uri(AbsolutePath).AbsoluteUri}#L{Start.Value}:{End.Value}");

        public string Lines => Start == End ? $"line {Start.Value}" : $"lines {Start.Value}-{End.Value}";
    }

    /// <summary>Selected in a diff: the lines may be ones the change removed, which the file no
    /// longer has.</summary>
    public sealed record InDiff(DiffSelectionQuote Quote) : CodeQuote(Quote.Text);

    /// <summary>The quote of a selection in a document, or null for a bare caret.</summary>
    public static InFile? Of(string absolutePath, TextRange range, Func<TextRange, string> slice)
    {
        if (range.Start == range.End) return null;
        var start = range.Start.Line;
        // A selection that ends at the start of a line takes none of it.
        var end = range.End.Column.Value == 0 && range.End.Line.Value > start.Value
            ? new FileLine(range.End.Line.Value - 1)
            : range.End.Line;
        var text = slice(range);
        if (end.Value - start.Value >= MaxLines)
        {
            end = new FileLine(start.Value + MaxLines - 1);
            text = slice(new TextRange(range.Start, TextPosition.At(end.Value + 1, 0)));
        }

        return text.Trim().Length == 0 ? null : new InFile(absolutePath, start, end, text.TrimEnd('\r', '\n'));
    }

    /// <summary>Where the quote is, as a reader cites it: <c>path:12-18</c>. <paramref name="pathOf"/>
    /// turns an absolute path into the one the reader is shown.</summary>
    public string Location(Func<string, string> pathOf) => this switch
    {
        InFile file => file.Start == file.End
            ? $"{pathOf(file.AbsolutePath)}:{file.Start.Value}"
            : $"{pathOf(file.AbsolutePath)}:{file.Start.Value}-{file.End.Value}",
        InDiff diff => diff.Quote.Location,
        _ => throw new InvalidOperationException("Unknown quote."),
    };

    /// <summary>The quote as markdown for a prompt: where it is, then the code.</summary>
    public string ToMarkdown(Func<string, string> pathOf) => this switch
    {
        InFile file => Fenced(new StringBuilder()
            .Append("Selected in `").Append(pathOf(file.AbsolutePath)).Append("`, ").Append(file.Lines).Append(':'), file.Text),
        InDiff diff => diff.Quote.ToPrompt(null),
        _ => throw new InvalidOperationException("Unknown quote."),
    };

    private static string Fenced(StringBuilder lead, string text)
    {
        var fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return lead.Append("\n\n").Append(fence).Append('\n').Append(text).Append('\n').Append(fence).ToString();
    }
}

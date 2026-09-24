using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Pairing;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

/// <summary>The agent's code is colored as the file would read with it taken.</summary>
public sealed class DraftColorsTests
{
    private static readonly string[] File = ["a", "b", "c", "d"];

    [Fact]
    public void AnInsertion_IsColoredInsideTheFile_AtItsPlace()
    {
        var highlighter = new LineMarkingHighlighter();

        var spans = DraftColors.Of("x.ts", File, new DraftPlace.InsertAfter(new FileLine(2)), ["X", "Y"], highlighter);

        Assert.Equal("a\nb\nX\nY\nc\nd", highlighter.Text);
        Assert.Equal([2, 3], spans!.Select(line => line[0].Start));
    }

    [Fact]
    public void AReplacement_IsColoredInPlaceOfTheLinesItReplaces()
    {
        var highlighter = new LineMarkingHighlighter();

        var spans = DraftColors.Of("x.ts", File, new DraftPlace.Replace(new LineSpan(2, 3)), ["X"], highlighter);

        Assert.Equal("a\nX\nd", highlighter.Text);
        Assert.Equal([1], spans!.Select(line => line[0].Start));
    }

    [Fact]
    public void ANewFilesFirstBlock_IsColoredOnItsOwn()
    {
        var highlighter = new LineMarkingHighlighter();

        var spans = DraftColors.Of("x.ts", [], new DraftPlace.Replace(new LineSpan(1, 1)), ["X", "Y"], highlighter);

        Assert.Equal("X\nY", highlighter.Text);
        Assert.Equal(2, spans!.Count);
    }

    /// <summary>Colors each line with one span whose start is the line's 0-based index.</summary>
    private sealed class LineMarkingHighlighter : ISyntaxHighlighter
    {
        public string? Text { get; private set; }

        public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, FileLanguage language)
        {
            Text = fileText;
            return fileText.Split('\n').Select((_, i) => (IReadOnlyList<TokenSpan>)[new TokenSpan(i, 1, TokenColorSlot.Keyword)]).ToArray();
        }
    }
}

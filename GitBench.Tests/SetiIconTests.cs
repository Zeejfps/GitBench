using GitBench.Controls;
using GitBench.Features.CodeIntel;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The file browser's language marks. The set is tied to what the parser supports, so the tree tells
/// one truth: a row wearing a language mark is a row whose declarations it can open.
/// </summary>
public class SetiIconTests
{
    [Fact]
    public void EveryParsedLanguageHasAMark()
    {
        var missing = CodeLanguages.All.Where(l => string.IsNullOrEmpty(SetiIcons.For(l))).ToArray();

        Assert.Empty(missing);
    }

    // Every mark is a distinct glyph — two languages sharing one would make the icon say less than
    // the extension it replaced.
    [Fact]
    public void NoTwoLanguagesShareAMark()
    {
        var marks = CodeLanguages.All.Select(SetiIcons.For).ToArray();

        Assert.Equal(marks.Length, marks.Distinct().Count());
    }

    // The marks live in the Seti font's private use area; a codepoint outside it would be a
    // transcription slip that renders as a missing glyph rather than failing anywhere.
    [Fact]
    public void EveryMarkIsOneGlyphInThePrivateUseArea()
    {
        foreach (var language in CodeLanguages.All)
        {
            var mark = SetiIcons.For(language)!;
            Assert.Equal(1, mark.Length);
            Assert.InRange(mark[0], (char)0xE000, (char)0xF8FF);
        }
    }
}

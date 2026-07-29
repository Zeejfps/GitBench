using GitBench.Features.Markdown.Parsing;
using Xunit;

namespace GitBench.Tests.Markdown;

/// <summary>
/// Step 2 contract for <see cref="InlineParser.Parse"/> — the scoped inline subset resolved into
/// flat <see cref="InlineRun"/> lists. Where the plan leaves room, these tests pin the behavior;
/// the pins are binding on the implementation:
///
/// <list type="bullet">
/// <item>Hard break: two or more trailing spaces before a newline yield a run whose Text is
/// exactly "\n", always unstyled (no flags, no link) — there is no LineBreak node in the AST, so
/// the renderer treats a lone "\n" run as a break. The break-forming spaces are consumed. Soft
/// breaks (a bare "\n") collapse to a single space, taking the line's trailing spaces with them,
/// so no run text but a hard break ever contains a "\n"; trailing spaces at end of input (no
/// newline after them) stay literal.</item>
/// <item>Merging: parsing never emits two adjacent runs with identical
/// (Bold, Italic, Code, Strikethrough, LinkUrl), and never emits an empty-text run. Hard-break
/// "\n" runs are the sole exception — they never merge into their neighbors.</item>
/// <item>Precedence: code spans bind first, then links and autolinks, then
/// emphasis/strikethrough. Backslash escapes apply everywhere outside code spans; inside a code
/// span every character is verbatim.</item>
/// <item>Code spans (CommonMark basics): a backtick run closes at the next run of the same
/// length; longer runs delimit content containing backticks; one leading and one trailing space
/// are stripped when both are present and the content is not all spaces.</item>
/// <item>Emphasis: a delimiter run followed by whitespace cannot open, one preceded by
/// whitespace cannot close. Intraword underscores never emphasize (snake_case stays literal);
/// intraword asterisks do. Delimiters match across the whole input, including across soft
/// breaks. Nesting flattens to style flags on flat runs.</item>
/// <item>Links: [text](url) with inline styles allowed inside the text; the URL is taken
/// verbatim to the matching ')' and is never styled; no title syntax; no nested links — the
/// inner link wins and the outer brackets stay literal.</item>
/// <item>Autolinks: lowercase "http://" or "https://" with at least one character after "://",
/// terminated by whitespace or end of input. Trailing characters from the set .,;:!?) are
/// trimmed repeatedly (no paren balancing).</item>
/// <item>Unmatched or misused delimiters stay literal. Empty input yields an empty list. Parse
/// never throws.</item>
/// </list>
///
/// Theory rows encode expected runs as "FLAGS:text" pieces joined by '¦', where FLAGS is "-"
/// (plain) or a subset of B(old) I(talic) C(ode) S(trikethrough); L marks an autolink run whose
/// LinkUrl equals its Text. Explicit [text](url) links are asserted in facts.
/// </summary>
public class InlineParserTests
{
    private static IReadOnlyList<InlineRun> Parse(string text) => InlineParser.Parse(text);

    private static InlineRun[] ExpectedRuns(string spec)
    {
        var pieces = spec.Split('¦');
        var runs = new InlineRun[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            var colon = pieces[i].IndexOf(':');
            var flags = pieces[i][..colon];
            var text = pieces[i][(colon + 1)..];
            runs[i] = new InlineRun(
                text,
                Bold: flags.Contains('B'),
                Italic: flags.Contains('I'),
                Code: flags.Contains('C'),
                Strikethrough: flags.Contains('S'),
                LinkUrl: flags.Contains('L') ? text : null);
        }
        return runs;
    }

    private static void AssertRuns(string markdown, string spec)
        => Assert.Equal(ExpectedRuns(spec), Parse(markdown));

    // Merge/no-empty invariant: hard-break "\n" runs are the sole runs allowed to sit next to a
    // style-identical neighbor.
    private static void AssertWellFormed(IReadOnlyList<InlineRun> runs)
    {
        Assert.All(runs, r => Assert.NotEqual(string.Empty, r.Text));
        for (var i = 1; i < runs.Count; i++)
        {
            if (runs[i].Text == "\n" || runs[i - 1].Text == "\n") continue;
            var a = runs[i - 1];
            var b = runs[i];
            var same = a.Bold == b.Bold && a.Italic == b.Italic && a.Code == b.Code
                       && a.Strikethrough == b.Strikethrough && a.LinkUrl == b.LinkUrl;
            Assert.False(same, $"adjacent runs {i - 1} and {i} share identical style and must merge");
        }
    }

    // -------------------------------------------------------------- plain text

    [Fact]
    public void EmptyInputYieldsEmptyList()
    {
        Assert.Empty(Parse(""));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("a + b = c, right?")]
    [InlineData("no markup 100% of the time")]
    public void TextWithoutMarkupIsOnePlainRun(string text)
    {
        var run = Assert.Single(Parse(text));
        Assert.Equal(new InlineRun(text), run);
    }

    [Fact]
    public void SoftBreakBecomesASpaceInsideRunText()
    {
        // A source line wrap is not a break the reader asked for: it renders as a space so the
        // paragraph reflows to the width it is drawn at.
        var run = Assert.Single(Parse("line one\nline two"));
        Assert.Equal(new InlineRun("line one line two"), run);
    }

    // ---------------------------------------------------------------- emphasis

    [Theory]
    [InlineData("**b**", "B:b")]
    [InlineData("__b__", "B:b")]
    [InlineData("a **b** c", "-:a ¦B:b¦-: c")]
    [InlineData("**lead** rest", "B:lead¦-: rest")]
    [InlineData("rest **tail**", "-:rest ¦B:tail")]
    [InlineData("**two words**", "B:two words")]
    [InlineData("*i*", "I:i")]
    [InlineData("_i_", "I:i")]
    [InlineData("a *i* c", "-:a ¦I:i¦-: c")]
    [InlineData("***bi***", "BI:bi")]
    [InlineData("***bi*** plain", "BI:bi¦-: plain")]
    public void EmphasisDelimitersProduceStyledRuns(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("**a *b* c**", "B:a ¦BI:b¦B: c")]
    [InlineData("*a **b** c*", "I:a ¦BI:b¦I: c")]
    [InlineData("**a _b_ c**", "B:a ¦BI:b¦B: c")]
    [InlineData("__a *b*__", "B:a ¦BI:b")]
    [InlineData("*a ~~b~~ c*", "I:a ¦IS:b¦I: c")]
    public void NestedEmphasisFlattensToFlagsOnFlatRuns(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("*a", "-:*a")]
    [InlineData("a**", "-:a**")]
    [InlineData("**a", "-:**a")]
    [InlineData("a_", "-:a_")]
    [InlineData("~~a", "-:~~a")]
    [InlineData("a * b * c", "-:a * b * c")] // space-flanked delimiters neither open nor close
    [InlineData("**a * b**", "B:a * b")] // unmatched inner delimiter stays literal inside bold
    public void UnmatchedDelimitersStayLiteral(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("snake_case_name", "-:snake_case_name")]
    [InlineData("a_b_c", "-:a_b_c")]
    [InlineData("intra_word", "-:intra_word")]
    public void IntrawordUnderscoresDoNotEmphasize(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("a*b*c", "-:a¦I:b¦-:c")]
    [InlineData("un*frigging*believable", "-:un¦I:frigging¦-:believable")]
    public void IntrawordAsterisksDoEmphasize(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void WordBoundaryUnderscoresStillEmphasize()
    {
        // Only *intraword* underscores are inert; "__init__" is flanked by input boundaries.
        AssertRuns("__init__", "B:init");
    }

    [Fact]
    public void DelimitersMatchAcrossSoftBreaks()
    {
        // One paragraph is one inline text: emphasis spans the line ending.
        AssertRuns("*a\nb*", "I:a b");
    }

    // ------------------------------------------------------------- inline code

    [Theory]
    [InlineData("`c`", "C:c")]
    [InlineData("a `c` b", "-:a ¦C:c¦-: b")]
    [InlineData("``code``", "C:code")]
    [InlineData("``a`b``", "C:a`b")] // longer run delimits content containing backticks
    [InlineData("`**x**`", "C:**x**")] // no emphasis inside code
    [InlineData("`a *b* _c_`", "C:a *b* _c_")]
    [InlineData("`a\\*`", "C:a\\*")] // escapes are not processed inside code
    [InlineData("`[a](u)`", "C:[a](u)")] // no links inside code
    public void CodeSpansAreVerbatimAndWinOverOtherMarkup(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("`` ` ``", "C:`")] // one space stripped from each side
    [InlineData("` a `", "C:a")]
    [InlineData("` `", "C: ")] // all-space content is not stripped
    public void CodeSpanStripsOneFlankingSpacePair(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("`a", "-:`a")]
    [InlineData("a`", "-:a`")]
    [InlineData("``a`", "-:``a`")] // closing run length must match the opener
    public void UnmatchedBackticksStayLiteral(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void EmphasisDelimiterInsideCodeSpanDoesNotPairWithOneOutside()
    {
        // Code binds first, so the outer '*' has no partner and stays literal.
        AssertRuns("*a `*` b", "-:*a ¦C:*¦-: b");
    }

    [Fact]
    public void CodeSpanInsideEmphasisKeepsTheEmphasisFlag()
    {
        // Code wins over emphasis *parsing*; a code span inside a matched emphasis pair still
        // carries the surrounding style flag.
        AssertRuns("*a `b` c*", "I:a ¦CI:b¦I: c");
    }

    // ----------------------------------------------------------- strikethrough

    [Theory]
    [InlineData("~~s~~", "S:s")]
    [InlineData("a ~~s~~ b", "-:a ¦S:s¦-: b")]
    [InlineData("~x~", "-:~x~")] // single tildes are not strikethrough
    [InlineData("**~~x~~**", "BS:x")]
    [InlineData("~~a **b**~~", "S:a ¦BS:b")]
    public void StrikethroughUsesDoubleTildes(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    // ------------------------------------------------------------------- links

    [Fact]
    public void SimpleLinkProducesOneLinkedRun()
    {
        Assert.Equal(
            new[] { new InlineRun("text", LinkUrl: "https://example.com") },
            Parse("[text](https://example.com)"));
    }

    [Fact]
    public void LinkSitsBetweenPlainRuns()
    {
        Assert.Equal(
            new[]
            {
                new InlineRun("see "),
                new InlineRun("docs", LinkUrl: "https://d.io"),
                new InlineRun(" now"),
            },
            Parse("see [docs](https://d.io) now"));
    }

    [Fact]
    public void InlineStylesResolveInsideLinkText()
    {
        Assert.Equal(
            new[]
            {
                new InlineRun("b", Bold: true, LinkUrl: "u"),
                new InlineRun(" c", LinkUrl: "u"),
            },
            Parse("[**b** c](u)"));
    }

    [Fact]
    public void CodeSpanInsideLinkTextKeepsTheLink()
    {
        Assert.Equal(
            new[] { new InlineRun("c", Code: true, LinkUrl: "u") },
            Parse("[`c`](u)"));
    }

    [Fact]
    public void EmphasisAroundLinkStylesTheLinkText()
    {
        // Links bind before emphasis; the surrounding pair still styles the linked run.
        Assert.Equal(
            new[] { new InlineRun("a", Bold: true, LinkUrl: "u") },
            Parse("**[a](u)**"));
    }

    [Fact]
    public void UrlIsVerbatimAndNeverStyled()
    {
        // Emphasis/underscore markup inside the URL is not parsed and does not leak styling.
        Assert.Equal(
            new[] { new InlineRun("a", LinkUrl: "https://e.com/*x*_y_") },
            Parse("[a](https://e.com/*x*_y_)"));
    }

    [Fact]
    public void EscapedBracketInsideLinkTextIsLiteral()
    {
        Assert.Equal(
            new[] { new InlineRun("a]b", LinkUrl: "u") },
            Parse("[a\\]b](u)"));
    }

    [Theory]
    [InlineData("[a]", "-:[a]")]
    [InlineData("[a](b", "-:[a](b")]
    [InlineData("[a] (b)", "-:[a] (b)")] // no space between ']' and '(' in this subset
    [InlineData("[a)", "-:[a)")]
    public void MalformedLinksStayLiteral(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void NestedLinksAreNotAllowedInnerWins()
    {
        Assert.Equal(
            new[]
            {
                new InlineRun("[a "),
                new InlineRun("b", LinkUrl: "u"),
                new InlineRun("](v)"),
            },
            Parse("[a [b](u)](v)"));
    }

    // --------------------------------------------------------------- autolinks

    [Theory]
    [InlineData("https://example.com", "L:https://example.com")]
    [InlineData("http://example.com", "L:http://example.com")]
    [InlineData("see https://e.com now", "-:see ¦L:https://e.com¦-: now")]
    [InlineData("https://e.com/path?q=1&x=2", "L:https://e.com/path?q=1&x=2")]
    public void BareHttpUrlsAutolink(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("https://e.com.", "L:https://e.com¦-:.")]
    [InlineData("https://e.com, then", "L:https://e.com¦-:, then")]
    [InlineData("is it https://e.com?", "-:is it ¦L:https://e.com¦-:?")]
    [InlineData("(https://e.com)", "-:(¦L:https://e.com¦-:)")]
    [InlineData("https://e.com/x).", "L:https://e.com/x¦-:).")] // trims repeatedly, no balancing
    [InlineData("at https://e.com; and http://f.io!", "-:at ¦L:https://e.com¦-:; and ¦L:http://f.io¦-:!")]
    public void AutolinkTrailingPunctuationIsTrimmed(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Theory]
    [InlineData("ftp://example.com", "-:ftp://example.com")] // only http/https
    [InlineData("example.com", "-:example.com")] // scheme required
    [InlineData("HTTPS://EXAMPLE.COM", "-:HTTPS://EXAMPLE.COM")] // lowercase scheme only
    [InlineData("http://", "-:http://")] // needs at least one char after ://
    public void NonAutolinkTextStaysLiteral(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void EmphasisAroundAutolinkStylesTheLinkedRun()
    {
        Assert.Equal(
            new[] { new InlineRun("https://e.com", Bold: true, LinkUrl: "https://e.com") },
            Parse("**https://e.com**"));
    }

    // ----------------------------------------------------------------- escapes

    [Theory]
    [InlineData("\\*not italic\\*", "-:*not italic*")]
    [InlineData("\\_x\\_", "-:_x_")]
    [InlineData("\\`not code`", "-:`not code`")] // escaped opener leaves the closer unmatched too
    [InlineData("\\[x](u)", "-:[x](u)")]
    [InlineData("\\~\\~x\\~\\~", "-:~~x~~")]
    [InlineData("a \\\\ b", "-:a \\ b")]
    [InlineData("\\\\", "-:\\")]
    [InlineData("\\a", "-:\\a")] // backslash before a non-escapable char stays literal
    public void BackslashEscapesProduceLiterals(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void EscapedBackslashDoesNotEscapeTheNextDelimiter()
    {
        AssertRuns("\\\\*i*", "-:\\¦I:i");
    }

    // ------------------------------------------------------------- hard breaks

    [Fact]
    public void TwoTrailingSpacesBeforeNewlineAreAHardBreak()
    {
        // Pinned representation: the break is a dedicated unstyled run of exactly "\n"; the
        // break-forming spaces are consumed.
        AssertRuns("a  \nb", "-:a¦-:\n¦-:b");
    }

    [Fact]
    public void ThreeOrMoreTrailingSpacesAreStillOneHardBreak()
    {
        AssertRuns("a    \nb", "-:a¦-:\n¦-:b");
    }

    [Fact]
    public void SingleTrailingSpaceIsASoftBreakAndDoesNotDoubleTheSpace()
    {
        // One space is too few for a hard break; it is the line's trailing whitespace and goes
        // away with the line ending, leaving the soft break's single space.
        var run = Assert.Single(Parse("a \nb"));
        Assert.Equal(new InlineRun("a b"), run);
    }

    [Fact]
    public void SoftAndHardBreaksProduceDifferentRuns()
    {
        // The two must never be confusable: only the hard break reaches the renderer as "\n".
        Assert.Equal(new[] { new InlineRun("a b") }, Parse("a\nb"));
        Assert.Equal(
            new[] { new InlineRun("a"), new InlineRun("\n"), new InlineRun("b") },
            Parse("a  \nb"));
    }

    [Fact]
    public void TrailingSpacesAtEndOfInputAreNotABreak()
    {
        var run = Assert.Single(Parse("a  "));
        Assert.Equal(new InlineRun("a  "), run);
    }

    [Fact]
    public void HardBreakRunDoesNotMergeWithStyledNeighbors()
    {
        AssertRuns("**a**  \n**b**", "B:a¦-:\n¦B:b");
    }

    [Fact]
    public void HardBreakInsideEmphasisStaysUnstyled()
    {
        // Pinned: the "\n" run never carries flags, even when the emphasis pair spans it.
        AssertRuns("*a  \nb*", "I:a¦-:\n¦I:b");
    }

    // ----------------------------------------------------------------- merging

    [Theory]
    [InlineData("**a****b**", "B:ab")]
    [InlineData("**a**__b__", "B:ab")] // different delimiters, same style: still one run
    [InlineData("\\*a\\* b", "-:*a* b")] // escape literals merge with surrounding text
    [InlineData("~~a~~~~b~~", "S:ab")]
    public void AdjacentRunsWithIdenticalStyleMerge(string markdown, string spec)
    {
        AssertRuns(markdown, spec);
    }

    [Fact]
    public void AdjacentLinksWithTheSameUrlMerge()
    {
        Assert.Equal(new[] { new InlineRun("ab", LinkUrl: "u") }, Parse("[a](u)[b](u)"));
    }

    [Fact]
    public void AdjacentLinksWithDifferentUrlsDoNotMerge()
    {
        Assert.Equal(
            new[] { new InlineRun("a", LinkUrl: "u"), new InlineRun("b", LinkUrl: "v") },
            Parse("[a](u)[b](v)"));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("**a** and *b* and `c`")]
    [InlineData("**a *b* c** tail")]
    [InlineData("[x](u) https://e.com.")]
    [InlineData("\\*lit\\* **b**")]
    [InlineData("a  \nb  \nc")]
    [InlineData("~~s~~ `code` _i_")]
    public void ParsingNeverEmitsAdjacentMergeableOrEmptyRuns(string markdown)
    {
        AssertWellFormed(Parse(markdown));
    }

    // -------------------------------------------------------------- robustness

    [Theory]
    [InlineData("**")]
    [InlineData("````")]
    [InlineData("[")]
    [InlineData("](")]
    [InlineData("~~~~")]
    [InlineData("\\")]
    [InlineData("*_`~[]()")]
    [InlineData("[*a](b`c** \\")]
    [InlineData("  \n  \n")]
    public void ParseNeverThrowsOnPathologicalInput(string markdown)
    {
        var runs = Parse(markdown);
        Assert.NotNull(runs);
        AssertWellFormed(runs);
    }

    [Fact]
    public void MixedConstructsResolveInDocumentedPrecedenceOrder()
    {
        // Code first (the backticked ** is inert), then the link, then emphasis around both.
        Assert.Equal(
            new[]
            {
                new InlineRun("a ", Bold: true),
                new InlineRun("**", Bold: true, Code: true),
                new InlineRun(" ", Bold: true),
                new InlineRun("l", Bold: true, LinkUrl: "u"),
            },
            Parse("**a `**` [l](u)**"));
    }
}

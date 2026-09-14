using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using ZGF.Geometry;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// A narrator pointing at the stacked diff: a focus lands on the line it named — loading the file,
/// opening a gap — and reports the text there; spotlights are drawn where their lines are, wherever
/// those lines move to.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class ReviewPresentationViewTests : IDisposable
{
    private readonly ReviewPresentationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // The last filler file sits well past the list's load margin, so nothing has loaded its diff
    // when the focus arrives; the lookup has to trigger the load and wait for it.
    [Fact]
    public void AFocusIntoAnUnloadedFileResolvesOnceItsDiffLoads()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        var path = ReviewPresentationFixture.Filler(ReviewPresentationFixture.FillerCount - 1);

        var resolution = _fixture.Focus(path, DiffLineSide.New, 2, window);

        var resolved = Assert.IsType<ReviewLineResolution.Resolved>(resolution);
        Assert.Equal($"filler {ReviewPresentationFixture.FillerCount - 1} changed", resolved.Text);
        Assert.Equal(path, window.ActiveFile.Value);
        var canvas = _fixture.Harness.Render();
        Assert.NotEmpty(ReviewPresentationFixture.TextRows(canvas, resolved.Text));
    }

    // Line 20 lies in the unexpanded gap between a.txt's two hunks: the focus opens the gap, and
    // the text it reports is the file's own line, which only the expansion could have supplied.
    [Fact]
    public void AFocusOnALineInsideACollapsedGapExpandsTheGap()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        var resolution = _fixture.Focus("a.txt", DiffLineSide.New, 20, window);

        var resolved = Assert.IsType<ReviewLineResolution.Resolved>(resolution);
        Assert.Equal("a line 20", resolved.Text);
        Assert.NotEmpty(ReviewPresentationFixture.TextRows(_fixture.Harness.Render(), "a line 20"));
    }

    [Fact]
    public void TheOldSideNamesTheRemovedLineAndTheNewSideItsReplacement()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        var before = Assert.IsType<ReviewLineResolution.Resolved>(_fixture.Focus("a.txt", DiffLineSide.Old, 5, window));
        var after = Assert.IsType<ReviewLineResolution.Resolved>(_fixture.Focus("a.txt", DiffLineSide.New, 5, window));

        Assert.Equal("a line 5", before.Text);
        Assert.Equal("a line 5 changed", after.Text);
    }

    // Past the end of the file: the EOF gap is opened to be sure, and the answer names the last
    // line the diff then holds so the narrator can correct itself.
    [Fact]
    public void ALineTheFileDoesNotHaveIsReportedWithItsNeighbours()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        var resolution = _fixture.Focus("a.txt", DiffLineSide.New, ReviewPresentationFixture.ALines + 1, window);

        var missing = Assert.IsType<ReviewLineResolution.NotInDiff>(resolution);
        Assert.Equal(new FileLine(ReviewPresentationFixture.ALines), missing.NearestBefore);
        Assert.Null(missing.NearestAfter);
    }

    [Fact]
    public void AFileOutsideTheReviewIsNoSuchFile()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        Assert.IsType<ReviewLineResolution.NoSuchFile>(_fixture.Focus("zzz.txt", DiffLineSide.New, 1, window));
    }

    [Fact]
    public void ASecondFocusSupersedesOneStillWaiting()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        var first = window.FocusLineAsync(
            new ReviewLineRef("a.txt", DiffLineSide.New, new FileLine(35)), CancellationToken.None);
        var second = _fixture.Focus("b.txt", DiffLineSide.New, 3, window);

        var superseded = Assert.IsType<ReviewLineResolution.Unavailable>(_fixture.Await(first));
        Assert.Contains("superseded", superseded.Reason, StringComparison.Ordinal);
        Assert.Equal("b line 3 changed", Assert.IsType<ReviewLineResolution.Resolved>(second).Text);
    }

    [Fact]
    public void ACancelledFocusUnhooks()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        using var cancel = new CancellationTokenSource();

        var task = window.FocusLineAsync(
            new ReviewLineRef(ReviewPresentationFixture.Filler(0), DiffLineSide.New, new FileLine(2)), cancel.Token);
        cancel.Cancel();

        Assert.Throws<TaskCanceledException>(() => _fixture.Await(task));
    }

    [Fact]
    public void NothingIsDrawnForTheSpotlightsWhileThereAreNone()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);

        var canvas = _fixture.Harness.Render();

        var styles = ReviewPresentationFixture.SpotlightStyles;
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.Band));
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.Wash));
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.PinBackground));
    }

    // The band is resolved from the line number at draw time, so it sits exactly on the row that
    // draws that line — and the text of the range comes back with the resolution.
    [Fact]
    public void ASpotlightBandCoversTheRowsOfItsLinesAndPinsTheFirst()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        _fixture.Focus("a.txt", DiffLineSide.New, 5, window);

        var resolutions = _fixture.Await(window.SetSpotlightsAsync(
            [new ReviewSpotlight("a.txt", DiffLineSide.New, new FileLine(6), new FileLine(7))],
            dim: false,
            CancellationToken.None));
        var canvas = _fixture.Harness.Render();

        var resolved = Assert.IsType<ReviewLineResolution.Resolved>(Assert.Single(resolutions));
        Assert.Equal("a line 6\na line 7", resolved.Text);

        var styles = ReviewPresentationFixture.SpotlightStyles;
        var bands = ReviewPresentationFixture.RectsOf(canvas, styles.Band);
        Assert.Equal(2, bands.Count);
        var row6 = Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 6"));
        var row7 = Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 7"));
        Assert.Contains(bands, b => SameRow(b, row6));
        Assert.Contains(bands, b => SameRow(b, row7));

        var pin = Assert.Single(ReviewPresentationFixture.RectsOf(canvas, styles.PinBackground));
        Assert.True(pin.Bottom >= row6.Bottom && pin.Top <= row6.Top, "the pin rides the range's first row");
        Assert.Contains(canvas.Texts, t => t.Inputs.Text == "1" && SameRow(t.Inputs.Position, pin));
    }

    // Folding the file takes its rows off screen: the pins move to the header band, and unfolding
    // puts the band back on the same file line, wherever that row now is.
    [Fact]
    public void ASpotlightFollowsItsLineThroughAFoldAndBack()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        _fixture.Focus("a.txt", DiffLineSide.New, 5, window);
        _fixture.Await(window.SetSpotlightsAsync(
            [new ReviewSpotlight("a.txt", DiffLineSide.New, new FileLine(6), new FileLine(6))],
            dim: false,
            CancellationToken.None));
        var styles = ReviewPresentationFixture.SpotlightStyles;

        window.ToggleFileViewed("a.txt");
        var folded = _fixture.Harness.Render();

        Assert.Empty(ReviewPresentationFixture.RectsOf(folded, styles.Band));
        Assert.Empty(ReviewPresentationFixture.TextRows(folded, "a line 6"));
        var pin = Assert.Single(ReviewPresentationFixture.RectsOf(folded, styles.PinBackground));
        var header = Assert.Single(folded.Texts, t => t.Inputs.Text == "a.txt").Inputs.Position;
        Assert.True(pin.Bottom >= header.Bottom && pin.Top <= header.Top, "the pin rides the folded header band");

        window.ToggleFileViewed("a.txt");
        var unfolded = _fixture.Harness.Render();

        var band = Assert.Single(ReviewPresentationFixture.RectsOf(unfolded, styles.Band));
        var row = Assert.Single(ReviewPresentationFixture.TextRows(unfolded, "a line 6"));
        Assert.True(SameRow(band, row), "the band is back on line 6's row");
    }

    // The wash is scoped to the spotlit file: every washed row is one of a.txt's, and b.txt right
    // below it — on screen at the same time — keeps its rows untouched.
    [Fact]
    public void DimWashesOnlyTheRestOfTheSpotlitFile()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        _fixture.Focus("a.txt", DiffLineSide.New, 35, window);

        _fixture.Await(window.SetSpotlightsAsync(
            [new ReviewSpotlight("a.txt", DiffLineSide.New, new FileLine(35), new FileLine(35))],
            dim: true,
            CancellationToken.None));
        var canvas = _fixture.Harness.Render();

        var styles = ReviewPresentationFixture.SpotlightStyles;
        var washes = ReviewPresentationFixture.RectsOf(canvas, styles.Wash);
        Assert.NotEmpty(washes);
        Assert.NotEmpty(ReviewPresentationFixture.TextRows(canvas, "b line 3 changed"));
        var bRows = canvas.Texts.Where(t => t.Inputs.Text.StartsWith("b line", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(bRows);
        Assert.All(washes, wash => Assert.DoesNotContain(bRows, t => SameRow(wash, t.Inputs.Position)));
        var lit = Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 35 changed"));
        Assert.DoesNotContain(washes, wash => SameRow(wash, lit));
        Assert.Contains(washes, wash => SameRow(wash, Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 36"))));
    }

    [Fact]
    public void ClearingRemovesEveryBandAndPin()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        _fixture.Focus("a.txt", DiffLineSide.New, 5, window);
        _fixture.Await(window.SetSpotlightsAsync(
            [new ReviewSpotlight("a.txt", DiffLineSide.New, new FileLine(6), new FileLine(7))],
            dim: true,
            CancellationToken.None));

        window.ClearSpotlights();
        var canvas = _fixture.Harness.Render();

        var styles = ReviewPresentationFixture.SpotlightStyles;
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.Band));
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.Wash));
        Assert.Empty(ReviewPresentationFixture.RectsOf(canvas, styles.PinBackground));
        Assert.Empty(window.Spotlights.Value);
    }

    // What the list reports back: the viewport's file and lines after a focus, and the reviewer's
    // selection as the quote the assistant menu would send.
    [Fact]
    public void TheWindowReportsTheVisibleLinesAndTheSelectionQuote()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        _fixture.Focus("a.txt", DiffLineSide.New, 5, window);

        var visible = Assert.IsType<ReviewVisibleLines>(window.Visible.Value);
        Assert.Equal("a.txt", visible.Path);
        Assert.True(visible.From <= new FileLine(5) && new FileLine(5) <= visible.To);
        Assert.Null(window.Selection.Value);

        var canvas = _fixture.Harness.Render();
        var from = Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 6"));
        var to = Assert.Single(ReviewPresentationFixture.TextRows(canvas, "a line 7"));
        var harness = _fixture.Harness;
        harness.MoveTo(from.Left + 1f, from.Center.Y);
        harness.Press();
        harness.MoveTo(to.Right - 1f, to.Center.Y);
        harness.MoveTo(to.Right, to.Center.Y);
        harness.Release();

        var quote = Assert.IsType<DiffSelectionQuote>(window.Selection.Value);
        Assert.Equal("a.txt", quote.Path);
        Assert.Equal(new FileLine(6), quote.StartLine);
        Assert.Equal(new FileLine(7), quote.EndLine);
        Assert.Contains("a line 6", quote.Text, StringComparison.Ordinal);
        Assert.Equal(DiffQuoteSide.Context, quote.Side);
    }

    private static bool SameRow(RectF a, RectF b) =>
        Math.Abs(a.Bottom - b.Bottom) < 0.5f && Math.Abs(a.Height - b.Height) < 0.5f;
}

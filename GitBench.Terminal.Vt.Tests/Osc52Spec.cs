using System.Text;
using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// OSC 52, the clipboard sequence. A program on the far end of a pseudo-terminal is not trusted, so
/// the load-bearing properties are what the engine refuses: a payload that is not base64 produces no
/// request rather than an exception, and a read is answered without ever being surfaced.
/// </summary>
public class Osc52Spec
{
    static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AWrite_ArrivesAsAClipboardRequest()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;c;{Base64("copied")}{St}");

        var request = Assert.Single(result.Clipboard.ToArray());
        Assert.Equal("copied", request.Text);
        Assert.Equal(ClipboardTarget.Clipboard, request.Target);
    }

    [Fact]
    public void AWrite_DoesNotLeakItsPayloadIntoTheGrid()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}52;c;{Base64("secret")}{St}after");

        Assert.Equal("after", engine.RowText(0));
    }

    [Fact]
    public void AnEmptySelectionCharacter_MeansTheClipboard()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;;{Base64("hi")}{St}");

        Assert.Equal(ClipboardTarget.Clipboard, Assert.Single(result.Clipboard.ToArray()).Target);
    }

    [Fact]
    public void ThePrimarySelection_IsReportedSeparatelyFromTheClipboard()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;p;{Base64("hi")}{St}");

        Assert.Equal(ClipboardTarget.Primary, Assert.Single(result.Clipboard.ToArray()).Target);
    }

    [Fact]
    public void UnicodeSurvivesTheRoundTrip()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;c;{Base64("naïve — Ω")}{St}");

        Assert.Equal("naïve — Ω", Assert.Single(result.Clipboard.ToArray()).Text);
    }

    // ---- what the engine refuses ----

    [Fact]
    public void APayloadThatIsNotBase64_ProducesNoRequestAndNoThrow()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;c;not!valid!base64{St}");

        Assert.Empty(result.Clipboard.ToArray());
    }

    [Fact]
    public void APayloadOfInvalidUtf8_ComesBackAsReplacementCharactersRatherThanThrowing()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;c;{Convert.ToBase64String([0xC3, 0x28])}{St}");

        Assert.NotEmpty(Assert.Single(result.Clipboard.ToArray()).Text);
    }

    [Fact]
    public void AReadRequest_IsAnsweredWithAnEmptyClipboardAndNeverSurfaced()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;c;?{St}");

        // Denied, but not with silence: a program that asks waits for the answer, and an empty
        // clipboard is a denial it cannot tell from an empty clipboard.
        Assert.Empty(result.Clipboard.ToArray());
        Assert.Equal($"{Osc}52;c;{St}", Encoding.ASCII.GetString(result.Response.ToArray()));
    }

    [Fact]
    public void AReadOfThePrimarySelection_IsAnsweredForThatSelection()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Osc}52;p;?{St}");

        Assert.Empty(result.Clipboard.ToArray());
        Assert.Equal($"{Osc}52;p;{St}", Encoding.ASCII.GetString(result.Response.ToArray()));
    }

    // ---- the property that breaks hand-rolled engines ----

    [Fact]
    public void ASequenceSplitAcrossFeeds_StillProducesOneRequest()
    {
        using var engine = EngineUnderTest.Create();
        var sequence = $"{Osc}52;c;{Base64("split")}{St}";

        var requests = new List<TerminalClipboardRequest>();
        foreach (var chunk in Chunks(sequence))
            requests.AddRange(engine.Feed(chunk).Clipboard.ToArray());

        Assert.Equal("split", Assert.Single(requests).Text);
    }

    [Fact]
    public void TwoWritesInOneFeed_ArriveInTheOrderTheProgramSentThem()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed(
            $"{Osc}52;c;{Base64("first")}{St}{Osc}52;c;{Base64("second")}{St}");

        var requests = result.Clipboard.ToArray();
        Assert.Equal(["first", "second"], requests.Select(r => r.Text));
    }

    [Fact]
    public void AFeedWithNoClipboardSequence_CarriesNothingAndStillEqualsNothing()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed(ReadOnlySpan<byte>.Empty);

        Assert.False(result.HasClipboardRequests);
        Assert.Equal(FeedResult.Nothing, result);
    }

    static IEnumerable<string> Chunks(string text) =>
        text.Select(character => character.ToString());
}

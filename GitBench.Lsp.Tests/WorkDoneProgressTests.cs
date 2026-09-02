using System.Text.Json;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The work-done progress conversation, both halves. This client advertises
/// <c>window.workDoneProgress</c> in its handshake, so a server may ask to open a progress token —
/// and answering that ask with "method not found" is not a polite no. It is this client breaking a
/// promise it made, and typescript-language-server treats the broken promise as fatal: it throws
/// out of its own message loop and the process exits, seconds after the first file is opened.
/// </summary>
public class WorkDoneProgressTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void ARequestToOpenAProgressToken_IsAccepted()
    {
        Assert.IsType<InboundReply.Ok>(
            ProcessLanguageServer.ReplyTo(LspMethod.CreateWorkDoneProgress));
    }

    // Everything else genuinely is not implemented, and saying so is the honest answer. The
    // difference is that we never claimed those.
    [Theory]
    [InlineData("workspace/configuration")]
    [InlineData("client/registerCapability")]
    [InlineData("workspace/applyEdit")]
    public void ARequestForSomethingThisClientNeverPromised_IsRefused(string method)
    {
        Assert.IsType<InboundReply.NotHandled>(ProcessLanguageServer.ReplyTo(new LspMethod(method)));
    }

    [Fact]
    public void AProgressReportCarryingAPercentage_SaysHowFarAlong()
    {
        var readiness = ProcessLanguageServer.ReadProgress(
            Json("""{"token":"idx","value":{"kind":"report","percentage":40}}"""));

        Assert.Equal(40, Assert.IsType<ServerReadiness.Indexing>(readiness).PercentComplete);
    }

    // The common case, and the one that used to be dropped on the floor: tsserver announces
    // "Initializing JS/TS language features…" with no number at all. Reading only the number left
    // a server that was plainly alive and busy looking exactly like one that had wedged — which is
    // what the two-minute silence timer then killed.
    [Fact]
    public void AProgressReportWithNoPercentage_StillSaysWorkIsUnderWay()
    {
        var readiness = ProcessLanguageServer.ReadProgress(
            Json("""{"token":"idx","value":{"kind":"begin","title":"Initializing JS/TS language features"}}"""));

        Assert.Null(Assert.IsType<ServerReadiness.Indexing>(readiness).PercentComplete);
    }

    // The end of the work is not work happening. Readiness past this point comes from the server
    // answering something real, which is the only thing that means questions get answers.
    [Fact]
    public void TheEndOfAProgressRun_IsNotWorkUnderWay()
    {
        Assert.Null(ProcessLanguageServer.ReadProgress(Json("""{"token":"idx","value":{"kind":"end"}}""")));
    }

    [Theory]
    [InlineData("""{"token":"idx"}""")]
    [InlineData("""{"token":"idx","value":"nonsense"}""")]
    [InlineData("""[]""")]
    [InlineData("""7""")]
    public void AProgressReportThatMakesNoSense_SaysNothingRatherThanGuessing(string payload)
    {
        Assert.Null(ProcessLanguageServer.ReadProgress(Json(payload)));
    }
}

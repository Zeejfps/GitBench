using System.Text;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// What a trace has to be able to answer. A language server failure looks the same from inside the
/// app whatever caused it — nothing on screen — so these are the questions the trace exists to
/// settle: was the document ever opened, which uri did the server answer about, and at which
/// version.
/// </summary>
public sealed class LspTraceTests
{
    [Fact]
    public async Task BothHalvesOfTheConversationAreRecorded()
    {
        var trace = new RecordingTrace();
        await using var fx = new LspFixture(trace: trace);

        var asked = fx.AskHover();
        var request = await fx.Server.NextRequest();
        await fx.Server.ReplyOk(request.Id, Wire.HoverJson("what it said"));
        await Wire.Answered(asked);

        Assert.Contains(trace.Headings, heading => heading.StartsWith("--> textDocument/hover"));
        Assert.Contains(trace.Headings, heading => heading.StartsWith("<-- result"));
    }

    [Fact]
    public void AWaveOfDiagnosticsIsHeadedByTheDocumentItIsAbout()
    {
        var heading = Heading(
            LspTraffic.FromServer,
            """
            {"jsonrpc":"2.0","method":"textDocument/publishDiagnostics",
             "params":{"uri":"file:///C:/repo/src/Greeter.cs","version":2,"diagnostics":[{},{}]}}
            """);

        Assert.Equal(
            "<-- textDocument/publishDiagnostics uri=file:///C:/repo/src/Greeter.cs version=2 diagnostics=2",
            heading);
    }

    /// <summary>The uri and version nest one level deeper on the way out than they do on the way
    /// back, and a trace that only knew one of the two shapes would say nothing about half the
    /// messages that matter.</summary>
    [Fact]
    public void OpeningADocumentIsHeadedByTheSameFieldsTheAnswerWillCarry()
    {
        var heading = Heading(
            LspTraffic.ToServer,
            """
            {"jsonrpc":"2.0","method":"textDocument/didOpen",
             "params":{"textDocument":{"uri":"file:///C:/repo/src/Greeter.cs","languageId":"csharp",
                                       "version":1,"text":"class Greeter {}"}}}
            """);

        Assert.Equal(
            "--> textDocument/didOpen uri=file:///C:/repo/src/Greeter.cs version=1",
            heading);
    }

    [Fact]
    public void ARequestAndItsAnswerCarryTheSameIdSoTheyCanBePairedUp()
    {
        Assert.Equal(
            "--> textDocument/hover id=7 uri=file:///a.rs",
            Heading(
                LspTraffic.ToServer,
                """{"id":7,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///a.rs"}}}"""));

        Assert.Equal("<-- result id=7", Heading(LspTraffic.FromServer, """{"id":7,"result":null}"""));
    }

    /// <summary>An error is named as one. A request that was refused minutes after it went out is
    /// the whole story of a feature that never appeared, and a heading that called it a result
    /// would hide it.</summary>
    [Fact]
    public void AnErrorIsNotHeadedAsAResult()
    {
        Assert.Equal(
            "<-- error id=3",
            Heading(LspTraffic.FromServer, """{"id":3,"error":{"code":-32801,"message":"still loading"}}"""));
    }

    /// <summary>A trace is a diagnostic aid. Bytes that are not a message are exactly what someone
    /// opens one to see, so they are headed and kept rather than dropped.</summary>
    [Fact]
    public void SomethingThatIsNotAMessageIsStillHeaded()
    {
        Assert.Equal("<-- (not json)", Heading(LspTraffic.FromServer, "Internal error, see log"));
    }

    /// <summary>One file per server, named after it, holding both the headings and the messages
    /// under them.</summary>
    [Fact]
    public void AServersTraceIsWrittenWhereItWasAskedFor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gitbench-lsp-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = new LspTraceFolder(folder).Open("csharp");
            trace.Note("launching csharp-ls");
            trace.Message(
                LspTraffic.ToServer,
                Encoding.UTF8.GetBytes(
                    """{"method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///a.cs","version":1}}}"""));
            trace.Dispose();

            var written = Directory.EnumerateFiles(folder).Single();
            var text = File.ReadAllText(written);

            Assert.EndsWith("-csharp.log", written);
            Assert.Contains("### launching csharp-ls", text);
            Assert.Contains("--> textDocument/didOpen uri=file:///a.cs version=1", text);
            // The message itself, under its heading: a heading says which message it was, and the
            // body is what says whether it carried what it should have.
            Assert.Contains("""{"method":"textDocument/didOpen",""", text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A folder that cannot be written to is not a reason to fail the launch of the server
    /// being traced.</summary>
    [Fact]
    public void ATraceThatCannotBeOpenedRecordsNothingRatherThanFailing()
    {
        var occupied = Path.GetTempFileName();
        try
        {
            var trace = LspTraceFile.Create(Path.Combine(occupied, "csharp.log"));

            Assert.Same(NoLspTrace.Instance, trace);
            trace.Note("this goes nowhere");
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    private static string Heading(LspTraffic direction, string json) =>
        LspTraceHeading.Of(direction, Encoding.UTF8.GetBytes(json));

    private sealed class RecordingTrace : ILspTrace
    {
        private readonly List<string> _headings = [];

        public IReadOnlyList<string> Headings
        {
            get { lock (_headings) return _headings.ToArray(); }
        }

        public void Message(LspTraffic direction, ReadOnlyMemory<byte> payload)
        {
            lock (_headings) _headings.Add(LspTraceHeading.Of(direction, payload));
        }

        public void Note(string text)
        {
            lock (_headings) _headings.Add($"### {text}");
        }

        public void Dispose() { }
    }
}

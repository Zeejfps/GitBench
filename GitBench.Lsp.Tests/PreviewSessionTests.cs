using Xunit;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// The one open document the Files pane holds: opened on preview, closed when the selection moves,
/// never edited. Everything a server sends is checked against the document that is open now —
/// diagnostics arrive in waves seconds apart and replace what came before, and an answer that
/// outlived its file is dropped rather than drawn on the next one.
/// </summary>
public sealed class PreviewSessionTests : IDisposable
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private readonly ScriptedLanguageClient _client = new();
    private readonly PreviewSession _session;

    private readonly DocumentUri _a = FileAt("src/main.rs");
    private readonly DocumentUri _b = FileAt("src/lib.rs");

    public PreviewSessionTests() => _session = new PreviewSession(_client, RepoBoundary.At(Root));

    public void Dispose() => _session.Dispose();

    private static DocumentUri FileAt(string relative) =>
        DocumentUri.OfFile(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static PreviewFile Rust(DocumentUri uri, string text) =>
        new(uri, LanguageId.Of("rust"), PreviewContent.Whole(text));

    private static Diagnostic Problem(string message) =>
        new(
            new LspRange(
                new LspPosition(new LspLine(0), new LspCharacter(0)),
                new LspPosition(new LspLine(0), new LspCharacter(1))),
            DiagnosticSeverity.Error,
            message);

    private static LspPosition Somewhere => new(new LspLine(1), new LspCharacter(4));

    private DocumentState.Open Open() => Assert.IsType<DocumentState.Open>(_session.State);

    private static string[] Messages(DiagnosticsState state) =>
        Assert.IsType<DiagnosticsState.Received>(state).Diagnostics.Select(d => d.Message).ToArray();

    [Fact]
    public void PreviewingAFileOpensItAndWaitsForTheFirstResults()
    {
        _session.Preview(Rust(_a, "fn main() {}"));

        var open = Open();
        Assert.Equal(_a, open.Uri);
        Assert.IsType<DiagnosticsState.Waiting>(open.Diagnostics);
        Assert.Equal(_a, Assert.Single(_client.Opened).Uri);
    }

    [Fact]
    public void MovingTheSelectionClosesTheOldDocumentBeforeOpeningTheNew()
    {
        _session.Preview(Rust(_a, "one"));
        _session.Preview(Rust(_b, "two"));

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.Equal(new[] { _a, _b }, _client.Opened.Select(o => o.Uri));
        Assert.Equal(_b, Open().Uri);
    }

    // Clicking down a directory faster than servers answer must not leave documents behind.
    [Fact]
    public void RapidSelectionChangesLeaveExactlyOneDocumentOpen()
    {
        var c = FileAt("src/other.rs");

        _session.Preview(Rust(_a, "one"));
        _session.Preview(Rust(_b, "two"));
        _session.Preview(Rust(c, "three"));

        Assert.Equal(3, _client.Opened.Count);
        Assert.Equal(new[] { _a, _b }, _client.Closed);
        Assert.Equal(c, Open().Uri);
    }

    [Fact]
    public void PreviewingTheSameUnchangedFileDoesNotReopenIt()
    {
        _session.Preview(Rust(_a, "fn main() {}"));
        _client.Publish(_a, Problem("mismatched types"));

        _session.Preview(Rust(_a, "fn main() {}"));

        Assert.Single(_client.Opened);
        Assert.Empty(_client.Closed);
        Assert.Equal(new[] { "mismatched types" }, Messages(Open().Diagnostics));
    }

    // The file watcher and the selection take the same path: new content for the file on screen is
    // a close and a reopen at a new version, because we never send an edit.
    [Fact]
    public void AFileThatChangedOnDiskIsReopenedAtANewVersion()
    {
        _session.Preview(Rust(_a, "fn main() {}"));
        var before = Open().Version;

        _session.Preview(Rust(_a, "fn main() { changed(); }"));

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.Equal(2, _client.Opened.Count);
        Assert.NotEqual(before, Open().Version);
        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    // Over 2 MB the preview drops the tail and the last partial line, so what is on screen is not
    // the file. A server asked about it would answer about text that does not exist.
    [Fact]
    public void ATruncatedPreviewIsNeverSentToAServer()
    {
        _session.Preview(new PreviewFile(_a, LanguageId.Of("rust"), PreviewContent.Truncated));

        Assert.Equal(new DocumentState.NotSent(SkipReason.PreviewTruncated), _session.State);
        Assert.Empty(_client.Opened);
    }

    [Fact]
    public void AFileNoConfiguredServerHandlesIsNeverSent()
    {
        _session.Preview(new PreviewFile(_a, LanguageId.Of("cobol"), PreviewContent.Whole("IDENTIFICATION DIVISION.")));

        Assert.Equal(new DocumentState.NotSent(SkipReason.NoServerForLanguage), _session.State);
        Assert.Empty(_client.Opened);
    }

    [Fact]
    public void SelectingSomethingThatIsNotAFileClosesTheDocument()
    {
        _session.Preview(Rust(_a, "one"));

        _session.Clear();

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.IsType<DocumentState.Nothing>(_session.State);
    }

    [Fact]
    public void DisposingTheSessionClosesTheDocument()
    {
        _session.Preview(Rust(_a, "one"));

        _session.Dispose();

        Assert.Equal(new[] { _a }, _client.Closed);
    }

    // gopls sends type errors first and analyser warnings four seconds later; rust-analyzer sends
    // the same file three times as its check progresses. The last word wins outright.
    [Fact]
    public void EachWaveOfDiagnosticsReplacesTheOneBefore()
    {
        _session.Preview(Rust(_a, "one"));

        _client.Publish(_a, Problem("mismatched types"), Problem("unused import"));
        _client.Publish(_a, Problem("unreachable code"));

        Assert.Equal(new[] { "unreachable code" }, Messages(Open().Diagnostics));
    }

    // "No problems" and "not heard back yet" produce the same empty list on screen and must not be
    // the same state, or a file that is still being checked reads as clean.
    [Fact]
    public void AnEmptyWaveMeansNoProblemsNotNoAnswer()
    {
        _session.Preview(Rust(_a, "one"));
        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);

        _client.Publish(_a);

        Assert.Empty(Assert.IsType<DiagnosticsState.Received>(Open().Diagnostics).Diagnostics);
    }

    [Fact]
    public void DiagnosticsForAFileThatWasNeverOpenedAreIgnored()
    {
        _session.Preview(Rust(_a, "one"));

        _client.Publish(_b, Problem("mismatched types"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public void DiagnosticsForAVersionOlderThanTheOpenOneAreDropped()
    {
        _session.Preview(Rust(_a, "one"));
        var stale = Open().Version;
        _session.Preview(Rust(_a, "two"));

        _client.Publish(_a, ResultVersion.At(stale), Problem("mismatched types"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public void DiagnosticsTaggedWithTheOpenVersionAreApplied()
    {
        _session.Preview(Rust(_a, "one"));

        _client.Publish(_a, ResultVersion.At(Open().Version), Problem("mismatched types"));

        Assert.Equal(new[] { "mismatched types" }, Messages(Open().Diagnostics));
    }

    [Fact]
    public void DiagnosticsArrivingAfterTheDocumentClosedAreIgnored()
    {
        _session.Preview(Rust(_a, "one"));
        _session.Clear();

        _client.Publish(_a, Problem("mismatched types"));

        Assert.IsType<DocumentState.Nothing>(_session.State);
    }

    [Fact]
    public void ComingBackToAFileWaitsForFreshResultsRatherThanShowingTheOldOnes()
    {
        _session.Preview(Rust(_a, "one"));
        _client.Publish(_a, Problem("mismatched types"));
        _session.Preview(Rust(_b, "two"));

        _session.Preview(Rust(_a, "one"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public async Task AnAnswerForTheFileStillOnScreenIsApplied()
    {
        _session.Preview(Rust(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _client.Hovers.Single().Answer(new HoverReply(new HoverPayload.PlainText("i32"), OptionalRange.Absent));

        var content = Assert.IsType<HoverAnswer.Content>(await hover);
        Assert.Equal("i32", content.Text.Markdown);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSelectionMovedIsDiscarded()
    {
        _session.Preview(Rust(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _session.Preview(Rust(_b, "two"));
        _client.Hovers.Single().Answer(new HoverReply(new HoverPayload.PlainText("i32"), OptionalRange.Absent));

        Assert.IsType<HoverAnswer.Stale>(await hover);
    }

    [Fact]
    public async Task AnAnswerForAFileThatWasReopenedSinceIsDiscarded()
    {
        _session.Preview(Rust(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _session.Preview(Rust(_a, "two"));
        _client.Hovers.Single().Answer(new HoverReply(new HoverPayload.PlainText("i32"), OptionalRange.Absent));

        Assert.IsType<HoverAnswer.Stale>(await hover);
    }

    [Fact]
    public async Task ADefinitionWithNoLocationsIsNotFoundRatherThanAnEmptyJump()
    {
        _session.Preview(Rust(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _client.Definitions.Single().Answer(DefinitionPayload.Nothing);

        Assert.IsType<DefinitionAnswer.Nowhere>(await definition);
    }

    [Fact]
    public async Task ADefinitionForTheFileStillOnScreenIsApplied()
    {
        _session.Preview(Rust(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _client.Definitions.Single().Answer(
            new DefinitionPayload.Single(new Location(_b, new LspRange(Somewhere, Somewhere))));

        var targets = Assert.IsType<DefinitionAnswer.Targets>(await definition);
        Assert.Equal("src/lib.rs", Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(targets.Items)).RelativePath);
    }

    [Fact]
    public async Task ADefinitionThatArrivesAfterTheSelectionMovedIsDiscarded()
    {
        _session.Preview(Rust(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _session.Preview(Rust(_b, "two"));
        _client.Definitions.Single().Answer(
            new DefinitionPayload.Single(new Location(_a, new LspRange(Somewhere, Somewhere))));

        Assert.IsType<DefinitionAnswer.Stale>(await definition);
    }

    // Two requests outstanding at once: the one for the file on screen still counts.
    [Fact]
    public async Task AnOutstandingRequestForAnOldFileDoesNotSpoilTheAnswerForTheNewOne()
    {
        _session.Preview(Rust(_a, "one"));
        var first = _session.HoverAsync(Somewhere);
        _session.Preview(Rust(_b, "two"));
        var second = _session.HoverAsync(Somewhere);

        _client.Hovers[1].Answer(new HoverReply(new HoverPayload.PlainText("second"), OptionalRange.Absent));
        _client.Hovers[0].Answer(new HoverReply(new HoverPayload.PlainText("first"), OptionalRange.Absent));

        Assert.Equal("second", Assert.IsType<HoverAnswer.Content>(await second).Text.Markdown);
        Assert.IsType<HoverAnswer.Stale>(await first);
    }

    // Not just ignored on arrival — the server is told to stop, so a rust-analyzer request nobody
    // will read is not still running thirty seconds later.
    [Fact]
    public void MovingTheSelectionCancelsTheRequestsForTheFileLeftBehind()
    {
        _session.Preview(Rust(_a, "one"));
        _ = _session.HoverAsync(Somewhere);

        _session.Preview(Rust(_b, "two"));

        Assert.True(_client.Hovers.Single().Cancel.IsCancellationRequested);
    }

    [Fact]
    public async Task AskingAboutAPositionWithNothingOpenIsDiscarded()
    {
        Assert.IsType<HoverAnswer.Stale>(await _session.HoverAsync(Somewhere));
        Assert.IsType<DefinitionAnswer.Stale>(await _session.DefinitionAsync(Somewhere));
        Assert.Empty(_client.Hovers);
    }
}

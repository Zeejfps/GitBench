using GitBench.Lsp.Configuration;
using Xunit;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// The one open document the Files pane holds: opened on preview, closed when the selection moves,
/// changed in place as it is edited where the server follows edits. Everything a server sends is checked against the document that is open now —
/// diagnostics arrive in waves seconds apart and replace what came before, and an answer that
/// outlived its file is dropped rather than drawn on the next one.
/// </summary>
public sealed class PreviewSessionTests : IDisposable
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static readonly LanguageServerEntry Rust = new(
        LanguageId.Of("rust"),
        "rust-analyzer",
        Args: [],
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(5),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private readonly ScriptedLanguageServer _client = new();
    private readonly PreviewSession _session;

    private readonly DocumentUri _a = FileAt("src/main.rs");
    private readonly DocumentUri _b = FileAt("src/lib.rs");

    public PreviewSessionTests() =>
        _session = new PreviewSession(
            _client, Rust, RepoBoundary.At(Root), AskAgainPolicy.Default, (_, _) => Task.CompletedTask);

    public void Dispose() => _session.Dispose();

    private static DocumentUri FileAt(string relative) =>
        DocumentUri.OfFile(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static PreviewFile File(DocumentUri uri, string text) => new(uri, PreviewContent.Whole(text));

    private static Location At(DocumentUri uri) => new(uri, new LspRange(Somewhere, Somewhere));

    private static Definition.Targets Declared(DocumentUri uri) =>
        new([new DefinitionLocation(uri, new LspRange(Somewhere, Somewhere), new LspRange(Somewhere, Somewhere), OptionalRange.Absent)]);

    private static Hover.Text Plain(string text) => new(MarkupKind.Markdown, text, null);

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
        _session.Preview(File(_a, "fn main() {}"));

        var open = Open();
        Assert.Equal(_a, open.Uri);
        Assert.IsType<DiagnosticsState.Waiting>(open.Diagnostics);
        Assert.Equal(_a, Assert.Single(_client.Opened).Uri);
    }

    [Fact]
    public void MovingTheSelectionClosesTheOldDocumentBeforeOpeningTheNew()
    {
        _session.Preview(File(_a, "one"));
        _session.Preview(File(_b, "two"));

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.Equal(new[] { _a, _b }, _client.Opened.Select(o => o.Uri));
        Assert.Equal(_b, Open().Uri);
    }

    // Clicking down a directory faster than servers answer must not leave documents behind.
    [Fact]
    public void RapidSelectionChangesLeaveExactlyOneDocumentOpen()
    {
        var c = FileAt("src/other.rs");

        _session.Preview(File(_a, "one"));
        _session.Preview(File(_b, "two"));
        _session.Preview(File(c, "three"));

        Assert.Equal(3, _client.Opened.Count);
        Assert.Equal(new[] { _a, _b }, _client.Closed);
        Assert.Equal(c, Open().Uri);
    }

    [Fact]
    public void PreviewingTheSameUnchangedFileDoesNotReopenIt()
    {
        _session.Preview(File(_a, "fn main() {}"));
        _client.Publish(_a, Problem("mismatched types"));

        _session.Preview(File(_a, "fn main() {}"));

        Assert.Single(_client.Opened);
        Assert.Empty(_client.Closed);
        Assert.Equal(new[] { "mismatched types" }, Messages(Open().Diagnostics));
    }

    // A server that does not follow edits is shown new content for the file on screen as a close
    // and a reopen at a new version.
    [Fact]
    public void AFileThatChangedOnDiskIsReopenedAtANewVersion()
    {
        _session.Preview(File(_a, "fn main() {}"));
        var before = Open().Version;

        _session.Preview(File(_a, "fn main() { changed(); }"));

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.Equal(2, _client.Opened.Count);
        Assert.NotEqual(before, Open().Version);
        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public void AnEditedFileIsChangedInPlaceWhereTheServerFollowsEdits()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        var before = Open().Version;

        _session.Preview(File(_a, "fn main() { typed(); }"));

        Assert.Single(_client.Opened);
        Assert.Empty(_client.Closed);
        var change = Assert.Single(_client.Changed);
        Assert.Equal("fn main() { typed(); }", change.Text);
        Assert.True(change.Version.Value > before.Value);
        Assert.Equal(change.Version, Open().Version);
    }

    // Dropping them to "waiting" on every keystroke would blink every squiggle off and back on.
    [Fact]
    public void AnEditKeepsTheDiagnosticsShownUntilTheNextWave()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        _client.Publish(_a, Problem("mismatched types"));

        _session.Preview(File(_a, "fn main() { typed(); }"));
        Assert.Equal(new[] { "mismatched types" }, Messages(Open().Diagnostics));

        _client.Publish(_a, ResultVersion.At(Open().Version), Problem("unresolved name"));
        Assert.Equal(new[] { "unresolved name" }, Messages(Open().Diagnostics));
    }

    [Fact]
    public void AWaveRecordsTheTextItsPositionsAreIn()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        _session.Preview(File(_a, "fn main() { typed(); }"));

        _client.Publish(_a, Problem("unresolved name"));

        var received = Assert.IsType<DiagnosticsState.Received>(Open().Diagnostics);
        Assert.Equal("fn main() { typed(); }", received.DescribedText);
    }

    [Fact]
    public void AWaveForTheTextBeforeAnEditIsDropped()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        var before = Open().Version;
        _session.Preview(File(_a, "fn main() { typed(); }"));

        _client.Publish(_a, ResultVersion.At(before), Problem("stale"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public async Task AnEditCancelsQuestionsAboutTheTextBeforeIt()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        var hover = _session.HoverAsync(Somewhere);
        var asked = Assert.Single(_client.Hovers);

        _session.Preview(File(_a, "fn main() { typed(); }"));
        asked.Answer(Plain("about the old text"));

        Assert.True(asked.Cancel.IsCancellationRequested);
        Assert.Null(await hover);
    }

    private static ServerCapabilities Syncing() =>
        new("fake", ServerCapabilities.Utf16, SupportsHover: true, SupportsDefinition: true, SupportsReferences: true)
        {
            TextSync = TextSync.Full,
        };

    // Over 2 MB the preview drops the tail and the last partial line, so what is on screen is not
    // the file. A server asked about it would answer about text that does not exist.
    [Fact]
    public void ATruncatedPreviewIsNeverSentToAServer()
    {
        _session.Preview(new PreviewFile(_a, PreviewContent.Truncated));

        Assert.IsType<DocumentState.Truncated>(_session.State);
        Assert.Empty(_client.Opened);
    }

    [Fact]
    public void SelectingSomethingThatIsNotAFileClosesTheDocument()
    {
        _session.Preview(File(_a, "one"));

        _session.Clear();

        Assert.Equal(new[] { _a }, _client.Closed);
        Assert.IsType<DocumentState.Nothing>(_session.State);
    }

    [Fact]
    public void DisposingTheSessionClosesTheDocument()
    {
        _session.Preview(File(_a, "one"));

        _session.Dispose();

        Assert.Equal(new[] { _a }, _client.Closed);
    }

    // gopls sends type errors first and analyser warnings four seconds later; rust-analyzer sends
    // the same file three times as its check progresses. The last word wins outright.
    [Fact]
    public void EachWaveOfDiagnosticsReplacesTheOneBefore()
    {
        _session.Preview(File(_a, "one"));

        _client.Publish(_a, Problem("mismatched types"), Problem("unused import"));
        _client.Publish(_a, Problem("unreachable code"));

        Assert.Equal(new[] { "unreachable code" }, Messages(Open().Diagnostics));
    }

    // "No problems" and "not heard back yet" produce the same empty list on screen and must not be
    // the same state, or a file that is still being checked reads as clean.
    [Fact]
    public void AnEmptyWaveMeansNoProblemsNotNoAnswer()
    {
        _session.Preview(File(_a, "one"));
        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);

        _client.Publish(_a);

        Assert.Empty(Assert.IsType<DiagnosticsState.Received>(Open().Diagnostics).Diagnostics);
    }

    [Fact]
    public void DiagnosticsForAFileThatWasNeverOpenedAreIgnored()
    {
        _session.Preview(File(_a, "one"));

        _client.Publish(_b, Problem("mismatched types"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public void DiagnosticsForAVersionOlderThanTheOpenOneAreDropped()
    {
        _session.Preview(File(_a, "one"));
        var stale = Open().Version;
        _session.Preview(File(_a, "two"));

        _client.Publish(_a, ResultVersion.At(stale), Problem("mismatched types"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public void DiagnosticsTaggedWithTheOpenVersionAreApplied()
    {
        _session.Preview(File(_a, "one"));

        _client.Publish(_a, ResultVersion.At(Open().Version), Problem("mismatched types"));

        Assert.Equal(new[] { "mismatched types" }, Messages(Open().Diagnostics));
    }

    [Fact]
    public void DiagnosticsArrivingAfterTheDocumentClosedAreIgnored()
    {
        _session.Preview(File(_a, "one"));
        _session.Clear();

        _client.Publish(_a, Problem("mismatched types"));

        Assert.IsType<DocumentState.Nothing>(_session.State);
    }

    [Fact]
    public void ComingBackToAFileWaitsForFreshResultsRatherThanShowingTheOldOnes()
    {
        _session.Preview(File(_a, "one"));
        _client.Publish(_a, Problem("mismatched types"));
        _session.Preview(File(_b, "two"));

        _session.Preview(File(_a, "one"));

        Assert.IsType<DiagnosticsState.Waiting>(Open().Diagnostics);
    }

    [Fact]
    public async Task AnAnswerForTheFileStillOnScreenIsApplied()
    {
        _session.Preview(File(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _client.Hovers.Single().Answer(Plain("i32"));

        Assert.Equal("i32", Assert.IsType<HoverText>(await hover).Markdown);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSelectionMovedIsDiscarded()
    {
        _session.Preview(File(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _session.Preview(File(_b, "two"));
        _client.Hovers.Single().Answer(Plain("i32"));

        Assert.Null(await hover);
    }

    [Fact]
    public async Task AnAnswerForAFileThatWasReopenedSinceIsDiscarded()
    {
        _session.Preview(File(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);

        _session.Preview(File(_a, "two"));
        _client.Hovers.Single().Answer(Plain("i32"));

        Assert.Null(await hover);
    }

    [Fact]
    public async Task ADefinitionWithNoLocationsIsNotFoundRatherThanAnEmptyJump()
    {
        _session.Preview(File(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _client.Definitions.Single().Answer(new Definition.None());

        Assert.Empty((await definition).Targets);
    }

    [Fact]
    public async Task ADefinitionForTheFileStillOnScreenIsApplied()
    {
        _session.Preview(File(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _client.Definitions.Single().Answer(Declared(_b));

        var targets = (await definition).Targets;
        Assert.Equal("src/lib.rs", Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(targets)).RelativePath);
    }

    // The span the answer resolved back in the asking file, read off the first target: several
    // targets for one symbol are alternative declarations of the same span, and a reader points at
    // one thing.
    [Fact]
    public async Task TheResolvedSpanIsReadOffTheFirstTarget()
    {
        _session.Preview(File(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        var first = LspRange.Empty(new LspPosition(new LspLine(3), new LspCharacter(12)));
        var second = LspRange.Empty(new LspPosition(new LspLine(9), new LspCharacter(1)));
        _client.Definitions.Single().Answer(new Definition.Targets(
        [
            new DefinitionLocation(_b, first, first, OptionalRange.Of(first)),
            new DefinitionLocation(_a, second, second, OptionalRange.Of(second)),
        ]));

        var origin = Assert.IsType<OptionalRange.Present>((await definition).Origin);
        Assert.Equal(new LspLine(3), origin.Range.Start.Line);
    }

    [Fact]
    public async Task ADefinitionThatArrivesAfterTheSelectionMovedIsDiscarded()
    {
        _session.Preview(File(_a, "one"));
        var definition = _session.DefinitionAsync(Somewhere);

        _session.Preview(File(_b, "two"));
        _client.Definitions.Single().Answer(Declared(_a));

        Assert.Empty((await definition).Targets);
    }

    [Fact]
    public async Task ASymbolNothingUsesIsFoundNowhereRatherThanAnEmptyList()
    {
        _session.Preview(File(_a, "one"));
        var references = _session.ReferencesAsync(Somewhere);

        _client.References.Single().Answer(new References.None());

        Assert.Empty(Assert.IsType<ReferenceReply.Answered>(await references).Sites);
    }

    [Fact]
    public async Task UsagesForTheFileStillOnScreenAreClassifiedAgainstTheRepository()
    {
        _session.Preview(File(_a, "one"));
        var references = _session.ReferencesAsync(Somewhere);

        _client.References.Single().Answer(new References.Sites([At(_b), At(_a)]));

        var sites = Assert.IsType<ReferenceReply.Answered>(await references);
        Assert.Equal(
            new[] { "src/lib.rs", "src/main.rs" },
            sites.Sites.Cast<DefinitionTarget.InRepo>().Select(site => site.RelativePath));
    }

    // A count drawn above a declaration in the file that is no longer open is a count of something
    // else entirely.
    [Fact]
    public async Task UsagesThatArriveAfterTheSelectionMovedAreDiscarded()
    {
        _session.Preview(File(_a, "one"));
        var references = _session.ReferencesAsync(Somewhere);

        _session.Preview(File(_b, "two"));
        _client.References.Single().Answer(new References.Sites([At(_a)]));

        Assert.IsType<ReferenceReply.Unavailable>(await references);
    }

    // The same file, edited underneath: a version the answer no longer describes.
    [Fact]
    public async Task UsagesThatArriveAfterTheFileChangedOnDiskAreDiscarded()
    {
        _session.Preview(File(_a, "one"));
        var references = _session.ReferencesAsync(Somewhere);

        _session.Preview(File(_a, "two"));
        _client.References.Single().Answer(new References.Sites([At(_a)]));

        Assert.IsType<ReferenceReply.Unavailable>(await references);
    }

    // Two requests outstanding at once: the one for the file on screen still counts.
    [Fact]
    public async Task AnOutstandingRequestForAnOldFileDoesNotSpoilTheAnswerForTheNewOne()
    {
        _session.Preview(File(_a, "one"));
        var first = _session.HoverAsync(Somewhere);
        _session.Preview(File(_b, "two"));
        var second = _session.HoverAsync(Somewhere);

        _client.Hovers[1].Answer(Plain("second"));
        _client.Hovers[0].Answer(Plain("first"));

        Assert.Equal("second", Assert.IsType<HoverText>(await second).Markdown);
        Assert.Null(await first);
    }

    // Not just ignored on arrival — the server is told to stop, so a rust-analyzer request nobody
    // will read is not still running thirty seconds later.
    [Fact]
    public void MovingTheSelectionCancelsTheRequestsForTheFileLeftBehind()
    {
        _session.Preview(File(_a, "one"));
        _ = _session.HoverAsync(Somewhere);

        _session.Preview(File(_b, "two"));

        Assert.True(_client.Hovers.Single().Cancel.IsCancellationRequested);
    }

    [Fact]
    public async Task AskingAboutAPositionWithNothingOpenIsDiscarded()
    {
        Assert.Null(await _session.HoverAsync(Somewhere));
        Assert.Empty((await _session.DefinitionAsync(Somewhere)).Targets);
        Assert.IsType<ReferenceReply.Unavailable>(await _session.ReferencesAsync(Somewhere));
        Assert.Empty(_client.Hovers);
    }

    /// <summary>
    /// A server that would not answer is not a server saying "nothing uses this". The distinction
    /// only matters here — a hover that fails shows no card and a definition that fails goes
    /// nowhere, but a usage count is a sentence about the code, and "no usages" over live code
    /// reads as a claim that it is dead.
    /// </summary>
    [Fact]
    public async Task AServerRefusingToAnswerIsNotAnAnswerOfZero()
    {
        _session.Preview(File(_a, "fn main() {}"));

        var refused = _session.ReferencesAsync(Somewhere);
        _client.References.Single().Refuse();
        Assert.IsType<ReferenceReply.Unavailable>(await refused);

        _client.Forget<References>();
        var answered = _session.ReferencesAsync(Somewhere);
        _client.References.Single().Answer(new References.None());
        Assert.Empty(Assert.IsType<ReferenceReply.Answered>(await answered).Sites);
    }

    /// <summary>
    /// Closing a file cancels its requests and disposes the source they were waiting on, and a
    /// request already on its way to the transport can reach it after that — where registering on
    /// the token it was handed throws rather than reporting cancellation. It means what a
    /// cancellation means, so it reads as one. One request at a time made this rare; a screenful
    /// of usage counts asked at once makes it ordinary.
    /// </summary>
    [Fact]
    public async Task ARequestThatOutlivesTheSourceItWasHandedIsDiscardedRatherThanThrowing()
    {
        _session.Preview(File(_a, "one"));
        var hover = _session.HoverAsync(Somewhere);
        var definition = _session.DefinitionAsync(Somewhere);
        var references = _session.ReferencesAsync(Somewhere);

        _session.Clear();
        _client.Hovers.Single().Fail(new ObjectDisposedException(nameof(CancellationTokenSource)));
        _client.Definitions.Single().Fail(new ObjectDisposedException(nameof(CancellationTokenSource)));
        _client.References.Single().Fail(new ObjectDisposedException(nameof(CancellationTokenSource)));

        Assert.Null(await hover);
        Assert.Empty((await definition).Targets);
        Assert.IsType<ReferenceReply.Unavailable>(await references);
    }

    private static LspPosition At(int line, int character) => new(new LspLine(line), new LspCharacter(character));

    // The server is asked about the file as it would read with the suggestion in it — where each
    // name is declared, and what a hover shows for the declared ones — then told the text it held
    // before: the reader never had the draft, so the server must not keep it.
    [Fact]
    public async Task ADraftIsAskedAboutAndTheFileIsPutBackAfter()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));

        var asked = _session.DefineInDraftAsync("fn main() { helper(); missing(); }", [At(0, 12), At(0, 22)], CancellationToken.None);
        var draft = Assert.Single(_client.Changed);
        Assert.Equal("fn main() { helper(); missing(); }", draft.Text);
        _client.Definitions[0].Answer(Declared(_b));
        _client.Definitions[1].Answer(new Definition.None());
        for (var i = 0; i < 500 && _client.Hovers.Count == 0; i++) await Task.Delay(1);
        Assert.Single(_client.Hovers).Answer(Plain("fn helper()"));

        var answers = Assert.IsType<DraftDefinitions.Answered>(await asked).Names;
        Assert.Equal("fn helper()", Assert.IsType<DraftDefinition.Declared>(answers[0]).Hover?.Markdown);
        Assert.IsType<DraftDefinition.Undeclared>(answers[1]);
        var back = _client.Changed[1];
        Assert.Equal("fn main() {}", back.Text);
        Assert.True(back.Version.Value > draft.Version.Value);
        Assert.Equal(back.Version, Open().Version);
    }

    // "Declared nowhere" says the code is missing something; a refusal says nothing about the code.
    [Fact]
    public async Task ARefusalAboutADraftNameIsUnansweredRatherThanUndeclared()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));

        var asked = _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);
        _client.Definitions.Single().Refuse();

        Assert.IsType<DraftDefinition.Unanswered>(Assert.Single(Assert.IsType<DraftDefinitions.Answered>(await asked).Names));
    }

    // The text the pane has never changed, so nothing watching it is told it did.
    [Fact]
    public async Task ADraftComingAndGoingPublishesNothing()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        var published = 0;
        _session.StateChanged += _ => published++;

        var asked = _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);
        _client.Definitions.Single().Answer(new Definition.None());
        await asked;

        Assert.Equal(0, published);
    }

    // The squiggles on screen are the reader's text's. A wave about the draft — while it is in, or
    // tagged with its version and arriving after — would draw its problems over lines it never had.
    [Fact]
    public async Task DiagnosticsAboutADraftAreNeverShown()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));
        _client.Publish(_a, Problem("before"));

        var asked = _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);
        var draftVersion = Assert.Single(_client.Changed).Version;
        _client.Publish(_a, Problem("about the draft"));
        _client.Definitions.Single().Answer(new Definition.None());
        await asked;
        _client.Publish(_a, ResultVersion.At(draftVersion), Problem("late, about the draft"));

        Assert.Equal(new[] { "before" }, Messages(Open().Diagnostics));
    }

    // Anything else asked while the draft is in would be answered about text the reader does not
    // have: a hover over the wrong line, a usage count for a draft.
    [Fact]
    public async Task NothingElseIsAskedWhileADraftIsIn()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));

        var asked = _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);
        var hover = await _session.HoverAsync(Somewhere);
        var again = await _session.DefineInDraftAsync("fn main() { other(); }", [At(0, 12)], CancellationToken.None);

        Assert.Null(hover);
        Assert.Empty(_client.Hovers);
        Assert.IsType<DraftDefinitions.Unavailable>(again);
        _client.Definitions.Single().Answer(new Definition.None());
        await asked;
    }

    // Without edits, the draft would have to be opened in place of the file and the file reopened
    // after, and each reopen resets the diagnostics on screen to waiting.
    [Fact]
    public async Task AServerThatDoesNotFollowEditsIsNotShownADraft()
    {
        _session.Preview(File(_a, "fn main() {}"));

        var asked = await _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);

        Assert.IsType<DraftDefinitions.Unavailable>(asked);
        Assert.Empty(_client.Changed);
        Assert.Single(_client.Opened);
    }

    [Fact]
    public async Task ADocumentClosedWhileADraftIsInIsNotPutBack()
    {
        _client.Capabilities = Syncing();
        _session.Preview(File(_a, "fn main() {}"));

        var asked = _session.DefineInDraftAsync("fn main() { helper(); }", [At(0, 12)], CancellationToken.None);
        _session.Clear();
        _client.Definitions.Single().Answer(new Definition.None());

        Assert.IsType<DraftDefinitions.Unavailable>(await asked);
        Assert.Single(_client.Changed);
        Assert.IsType<DocumentState.Nothing>(_session.State);
    }
}

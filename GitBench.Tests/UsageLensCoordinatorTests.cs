using GitBench.Features.Diff;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// What decides which declarations a language server is asked about, and what becomes of the
/// answers. A file has more declarations than fit on a screen and a server answers about a symbol
/// far more slowly than a reader scrolls past it, so nearly everything here is about not asking.
/// </summary>
public sealed class UsageLensCoordinatorTests
{
    private const string FileA = "/repo/src/AuthService.cs";
    private const string FileB = "/repo/src/Other.cs";

    // ---- what gets asked ----

    [Fact]
    public async Task OnlyTheDeclarationsOnScreenAreAskedAbout()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login"), world.Target("Reset")];
        world.Everywhere = [.. world.OnScreen, world.Target("Offscreen")];

        world.Refresh();
        await world.Settled();

        Assert.Equal(["Login", "Reset"], world.Asked);
    }

    [Fact]
    public async Task ADeclarationIsAskedAboutAtItsNameNotAtTheStartOfItsLine()
    {
        var world = new World();
        world.OnScreen = [world.TargetAt("Login", line: 7, column: 9)];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();

        Assert.Equal([(new FileLine(7), new RawColumn(9))], world.Servers.Positions);
    }

    /// <summary>
    /// The Files pane re-reads the open file about twice a minute. The declarations come back with
    /// the same containment paths, so their counts are already known and the reconcile tick costs
    /// nothing — without this it would put a screenful of questions to a server every thirty seconds
    /// for as long as the file stayed open.
    /// </summary>
    [Fact]
    public async Task AFileRereadDoesNotAskAgainAboutWhatIsAlreadyAnswered()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        await world.AnswerAll(sites: 3);

        world.Refresh();
        await world.Settled();

        Assert.Equal(["Login"], world.Asked);
    }

    [Fact]
    public async Task ScrollingAsksAboutTheDeclarationsThatCameIntoView()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = [world.Target("Login"), world.Target("Reset")];

        world.Refresh();
        await world.Settled();
        await world.AnswerAll(sites: 1);

        world.OnScreen = [world.Target("Reset")];
        world.Refresh();
        await world.Settled();

        Assert.Equal(["Login", "Reset"], world.Asked);
    }

    /// <summary>
    /// Scrolling abandons the wait before asking, never the questions already put. A reader who
    /// keeps scrolling would otherwise cancel every request just before it answered and see counts
    /// arrive only once they sat perfectly still.
    /// </summary>
    [Fact]
    public async Task AQuestionAlreadyPutSurvivesTheViewMovingAgain()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();

        world.OnScreen = [world.Target("Login"), world.Target("Reset")];
        world.Everywhere = world.OnScreen;
        world.Refresh();
        await world.Settled();

        await world.AnswerAll(sites: 2);

        Assert.Equal(new UsageLensState.Count(2), world.Published.On(world.LineOf("Login")));
    }

    // ---- what the answers say ----

    [Fact]
    public async Task AnAnswerIsPublishedAgainstTheLineTheLensSitsOn()
    {
        var world = new World();
        world.OnScreen = [world.TargetAt("Login", line: 7, column: 9)];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        Assert.Equal(new UsageLensState.Asking(), world.Published.On(world.LineOf("Login")));

        await world.AnswerAll(sites: 4);

        Assert.Equal(new UsageLensState.Count(4), world.Published.On(world.LineOf("Login")));
    }

    /// <summary>
    /// A server that answered with nothing is the real zero and says so. It is the one answer this
    /// feature must not guess at: "no usages" over a symbol nobody could ask about would read as
    /// dead code.
    /// </summary>
    [Fact]
    public async Task AServerAnsweringWithNothingIsAZeroAndAServerThatCouldNotBeAskedIsNot()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        await world.AnswerAll(sites: 0);
        Assert.Equal(new UsageLensState.Count(0), world.Published.On(world.LineOf("Login")));

        var other = new World();
        other.OnScreen = [other.Target("Login")];
        other.Everywhere = other.OnScreen;

        other.Refresh();
        await other.Settled();
        await other.FailAll();

        Assert.Equal(new UsageLensState.Unsupported(), other.Published.On(other.LineOf("Login")));
    }

    /// <summary>
    /// A refusal is retried when the servers change, and not before. A server that has failed
    /// refuses instantly and a refresh runs on every scroll, so retrying on a refresh would turn a
    /// broken server into a tight loop against it.
    /// </summary>
    [Fact]
    public async Task ADeclarationNobodyCouldBeAskedAboutIsAskedAgainOnlyOnARecheck()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        await world.FailAll();

        world.Refresh();
        await world.Settled();
        Assert.Equal(["Login"], world.Asked);

        world.Coordinator.Recheck();
        await world.Settled();

        Assert.Equal(["Login", "Login"], world.Asked);
    }

    /// <summary>
    /// A server asked before it has loaded the project around a file answers about that file alone
    /// — a number that looks right and is short. So a count is re-asked as the servers report
    /// progress, and the later, larger answer replaces the first one.
    /// </summary>
    [Fact]
    public async Task ACountIsReplacedWhenTheServerLaterKnowsMore()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        await world.AnswerAll(sites: 1);
        Assert.Equal(new UsageLensState.Count(1), world.Published.On(world.LineOf("Login")));

        world.Coordinator.Recheck();
        await world.Settled();
        await world.AnswerAll(sites: 3);

        Assert.Equal(new UsageLensState.Count(3), world.Published.On(world.LineOf("Login")));
    }

    /// <summary>
    /// Bounded, so a server that publishes diagnostics every few seconds cannot keep a screenful of
    /// declarations permanently in flight.
    /// </summary>
    [Fact]
    public async Task ADeclarationStopsBeingReaskedOnceItsAnswerHasHeld()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();

        for (var round = 0; round < 6; round++)
        {
            await world.AnswerAll(sites: 2);
            world.Coordinator.Recheck();
            await world.Settled();
        }

        Assert.Equal(3, world.Asked.Count);
        Assert.Equal(new UsageLensState.Count(2), world.Published.On(world.LineOf("Login")));
    }

    // ---- what happens when the file changes ----

    [Fact]
    public async Task AnotherFileForgetsWhatWasKnownAboutTheLastOne()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();
        await world.AnswerAll(sites: 3);
        Assert.Equal(new UsageLensState.Count(3), world.Published.On(world.LineOf("Login")));

        world.Path = FileB;
        world.OnScreen = [world.Target("Elsewhere")];
        world.Everywhere = world.OnScreen;
        world.Refresh();
        await world.Settled();

        Assert.Null(world.Published.On(world.LineOf("Login")));
        Assert.Equal(new UsageLensState.Asking(), world.Published.On(world.LineOf("Elsewhere")));
    }

    [Fact]
    public async Task AnAnswerAboutAFileThatHasLeftTheScreenIsDropped()
    {
        var world = new World();
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();

        // The next file has nothing to count, so nothing here publishes over the answer on its way
        // back — if it were kept, it would be drawn against the new file's line numbers.
        world.Path = FileB;
        world.OnScreen = [];
        world.Everywhere = [];
        world.Refresh();
        await world.Settled();
        // Answered raw, not through the waiting helper: this answer is meant to go nowhere, so
        // there is nothing to wait for. The pump is drained anyway, to give it every chance to
        // land somewhere it should not.
        world.Servers.AnswerAll(sites: 9);
        await world.Settled();
        await world.Settled();

        Assert.True(world.Published.IsEmpty);
    }

    // ---- when there is nobody to ask ----

    [Fact]
    public async Task AFileNoServerAnswersForGrowsNoRowsAndIsAskedNothing()
    {
        var world = new World { Servers = { Answers = false } };
        world.OnScreen = [world.Target("Login")];
        world.Everywhere = world.OnScreen;

        world.Refresh();
        await world.Settled();

        Assert.False(world.RowsShown);
        Assert.Empty(world.Asked);
        Assert.True(world.Published.IsEmpty);
    }

    [Fact]
    public void NoFileOnScreenPublishesNothing()
    {
        var world = new World { Path = null };

        world.Refresh();

        Assert.True(world.Published.IsEmpty);
        Assert.Empty(world.Asked);
    }

    /// <summary>The world the coordinator runs in: a server that parks every question until the
    /// test answers it, and a view whose visible rows the test decides.</summary>
    private sealed class World
    {
        private readonly Dictionary<string, UsageLensTarget> _targets = [];
        private readonly Dictionary<FileLine, string> _idOfLine = [];

        public World()
        {
            Coordinator = new UsageLensCoordinator(
                Servers,
                new ImmediateDispatcher(),
                () => Path,
                () => OnScreen,
                () => Everywhere,
                rows => RowsShown = rows,
                overlay => Published = overlay,
                settle: (_, _) => Task.CompletedTask);
        }

        public UsageLensCoordinator Coordinator { get; }

        public ScriptedReferences Servers { get; } = new();

        public string? Path { get; set; } = FileA;

        public IReadOnlyList<UsageLensTarget> OnScreen { get; set; } = [];

        public IReadOnlyList<UsageLensTarget> Everywhere { get; set; } = [];

        public UsageLensOverlay Published { get; private set; } = UsageLensOverlay.Empty;

        public bool RowsShown { get; private set; }

        /// <summary>The declarations the server was asked about, in order, named the way the test
        /// names them.</summary>
        public IReadOnlyList<string> Asked => [.. Servers.Positions.Select(p => _idOfLine[p.Line])];

        /// <summary>A declaration, on a line of its own. Stable per id, so naming the same one
        /// twice is the same declaration rather than a second one.</summary>
        public UsageLensTarget Target(string id) => TargetAt(id, _targets.Count + 1, column: 4);

        /// <summary>A declaration whose name sits somewhere the test cares about, for the cases
        /// that check which position a server was handed.</summary>
        public UsageLensTarget TargetAt(string id, int line, int column)
        {
            if (_targets.TryGetValue(id, out var known)) return known;

            var at = new FileLine(line);
            var target = new UsageLensTarget(id, at, at, new RawColumn(column));
            _targets[id] = target;
            _idOfLine[at] = id;
            return target;
        }

        public FileLine LineOf(string id) => Target(id).At;

        public void Refresh() => Coordinator.Refresh();

        // The settle is instant, so the questions are out by the time the pump has drained once.
        public async Task Settled()
        {
            await Task.Yield();
            await Task.Yield();
        }

        /// <summary>
        /// Answers everything outstanding and waits for the answers to reach the overlay. The wait
        /// is not ceremony: a question resumes on whichever thread the concurrency gate hands it
        /// back to, which under a loaded test run is not the one that answered it.
        /// </summary>
        public Task AnswerAll(int sites) => Settling(() => Servers.AnswerAll(sites));

        public Task FailAll() => Settling(Servers.FailAll);

        private async Task Settling(Action answer)
        {
            var awaiting = Servers.Outstanding;
            answer();

            for (var spin = 0; spin < 10_000; spin++)
            {
                if (awaiting.All(line => Published.On(line) is not (null or UsageLensState.Asking))) return;
                await Task.Yield();
            }

            throw new TimeoutException("the answers never reached the overlay");
        }
    }

    private sealed class ScriptedReferences : IReferenceSource
    {
        private readonly List<(FileLine Line, TaskCompletionSource<ReferenceReply> Completion)> _pending = [];

        public List<(FileLine Line, RawColumn Column)> Positions { get; } = [];

        public bool Answers { get; set; } = true;

        /// <summary>The declarations with a question outstanding, by the line each was asked
        /// about.</summary>
        public IReadOnlyList<FileLine> Outstanding => [.. _pending.Select(p => p.Line)];

        public bool CanReference(string absolutePath) => Answers;

        public Task<ReferenceReply> ReferencesAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct)
        {
            Positions.Add((line, column));
            var completion = new TaskCompletionSource<ReferenceReply>();
            _pending.Add((line, completion));
            return completion.Task;
        }

        public void AnswerAll(int sites)
        {
            var reply = new ReferenceReply.Answered(
                Enumerable.Range(0, sites)
                    .Select(_ => (DefinitionTarget)new DefinitionTarget.InRepo("x.cs", LspPosition.At(0, 0)))
                    .ToArray());
            Drain(reply);
        }

        public void FailAll() => Drain(ReferenceReply.Unavailable.Instance);

        private void Drain(ReferenceReply reply)
        {
            var pending = _pending.ToArray();
            _pending.Clear();
            foreach (var (_, completion) in pending) completion.TrySetResult(reply);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

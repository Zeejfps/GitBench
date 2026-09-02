using GitBench.Features.Diff;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// When the hover probe asks a language server, and when it stops asking.
/// </summary>
/// <remarks>
/// Every rule here was got wrong against a running app first, which is what these exist to prevent
/// a second time: a pointer resting still cancelling its own question forever, a card taken away
/// the moment anyone reached for it, and an answer to a question about one symbol shown over
/// another. None of it needs a server, a window, or a pointer.
/// </remarks>
public sealed class HoverProbeControllerTests
{
    private const string Root = "/repo";
    private const string File = "/repo/src/main.rs";

    [Fact]
    public async Task RestingOnASymbolAsksTheServerAboutIt()
    {
        var fx = new Fixture();

        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);

        var asked = Assert.Single(fx.Source.Asked);
        Assert.Equal(File, asked.Path);
        Assert.Equal(8, asked.Line.Value);
    }

    // The bug that made the feature look dead. A pointer sitting still still produces moves, and
    // treating each one as new abandons the question already in flight — forever, so it is asked
    // over and over and never answered. Asserting the number of questions does not catch it: the
    // abandoned ones are replaced. Asserting the answer survives does.
    [Fact]
    public async Task AnIdenticalMoveDoesNotAbandonTheQuestionAlreadyAsked()
    {
        var fx = new Fixture();
        fx.Source.Hold = true;

        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);
        fx.Move(10, 10);
        fx.Move(10, 10);
        fx.Source.ReleaseHeld();
        await fx.Settle();

        Assert.Single(fx.Source.Asked);
        Assert.Equal("line 8", fx.Presenter.Showing?.Markdown);
    }

    [Fact]
    public async Task MovingToAnotherSymbolAsksAboutThatOne()
    {
        var fx = new Fixture();
        fx.Positions[(10, 10)] = 8;
        fx.Positions[(10, 40)] = 12;

        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);
        fx.Move(10, 40);
        await fx.Settle(expectedAsks: 2);

        Assert.Equal([8, 12], fx.Source.Asked.Select(a => a.Line.Value));
    }

    [Fact]
    public async Task MovingOffTheTextTakesTheAnswerAway()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);
        Assert.NotNull(fx.Presenter.Showing);

        fx.Move(999, 999);

        Assert.Null(fx.Presenter.Showing);
    }

    // Reaching for the card means crossing the code it covers. Those moves must not be read as a
    // move to another symbol, or the card is gone before anyone arrives.
    [Fact]
    public async Task MovingOntoTheCardKeepsIt()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);
        var shown = fx.Presenter.Showing;

        // Below the anchor and within the card's width: where the card itself is.
        fx.Move(200, 10 - 100);
        await fx.Settle();

        Assert.Same(shown, fx.Presenter.Showing);
        Assert.Single(fx.Source.Asked);
    }

    // A server answers whatever it was asked, however long it takes. By then the pointer may be
    // somewhere else entirely, and an answer about the symbol it has left is worse than none.
    [Fact]
    public async Task AnAnswerThatArrivesAfterThePointerMovedOnIsNotShown()
    {
        var fx = new Fixture();
        fx.Source.Hold = true;

        fx.Move(10, 10);
        await fx.Settle(expectedAsks: 1);
        fx.Move(999, 999);
        fx.Source.ReleaseHeld();

        // Long enough for the answer to arrive and be turned away. Yielding is not enough here:
        // the assertion is that nothing appears, so the test has to outlive the thing that would
        // have appeared, or it passes because it looked too early.
        await Task.Delay(100);

        Assert.Null(fx.Presenter.Showing);
    }

    [Fact]
    public async Task AFileNoServerHandlesIsNeverAskedAbout()
    {
        var fx = new Fixture();
        fx.Source.HandlesAnything = false;

        fx.Move(10, 10);
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
        Assert.Null(fx.Presenter.Showing);
    }

    // While a project is still being indexed every answer is "ask again later", which is not an
    // error and must not put an empty card on screen.
    [Fact]
    public async Task AServerWithNothingToSayShowsNothing()
    {
        var fx = new Fixture();
        fx.Source.Answer = null;

        fx.Move(10, 10);
        await fx.Settle();

        Assert.Null(fx.Presenter.Showing);
    }

    [Fact]
    public async Task NoFileOnScreenMeansNoQuestion()
    {
        var fx = new Fixture();
        fx.Document = null;

        fx.Move(10, 10);
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
    }

    // A squiggle has to be readable, and the server that drew it may say nothing about the symbol
    // under the pointer — or there may be no server for the language at all any more.
    [Fact]
    public async Task ADiagnosticOnTheLineIsShownEvenWhenTheServerSaysNothingAboutTheSymbol()
    {
        var fixture = new Fixture();
        fixture.Source.Answer = null;
        fixture.Problems[8] = [Problem("cannot find value `x`")];

        fixture.Move(10, 10);
        await fixture.SettleShown();

        Assert.Contains("cannot find value `x`", fixture.Presenter.Showing?.Markdown);
    }

    [Fact]
    public async Task TheProblemComesBeforeTheTypeOnTheSameCard()
    {
        var fixture = new Fixture();
        fixture.Problems[8] = [Problem("mismatched types")];

        fixture.Move(10, 10);
        await fixture.SettleShown();

        var card = fixture.Presenter.Showing!.Markdown;
        Assert.Contains("line 8", card);
        Assert.True(card.IndexOf("mismatched types", StringComparison.Ordinal)
            < card.IndexOf("line 8", StringComparison.Ordinal));
    }

    private static Diagnostic Problem(string message) => new(
        new LspRange(
            new LspPosition(new LspLine(7), new LspCharacter(0)),
            new LspPosition(new LspLine(7), new LspCharacter(4))),
        DiagnosticSeverity.Error,
        message);

    private sealed class Fixture
    {
        public Fixture()
        {
            Positions[(10, 10)] = 8;
            Controller = new HoverProbeController(
                new Surface(this), Source, Presenter, new ImmediateDispatcher(),
                () => Document, (_, _) => Task.CompletedTask);
        }

        public Dictionary<(float X, float Y), int> Positions { get; } = new();

        public Dictionary<int, IReadOnlyList<Diagnostic>> Problems { get; } = new();

        public FakeSource Source { get; } = new();

        public FakePresenter Presenter { get; } = new();

        public HoverProbeController Controller { get; }

        public (string Root, string Path)? Document { get; set; } = (Root, File);

        public void Move(float x, float y) => Controller.PointerMovedTo(new PointF(x, y));

        /// <summary>
        /// Lets the asking task reach the server. Asking happens on its own task, so a test that
        /// only yields a fixed number of times races it — the fakes answer instantly, so this waits
        /// for the observable effect rather than for a duration.
        /// </summary>
        /// <summary>Waits for the card itself, not for the question: a test that asserts on what is
        /// on screen and waits for the asking passes or fails on how busy the machine is.</summary>
        public async Task SettleShown()
        {
            for (var i = 0; i < 500 && Presenter.Showing is null; i++)
            {
                await Task.Yield();
                await Task.Delay(1);
            }
        }

        public async Task Settle(int expectedAsks = 0)
        {
            // Nothing is expected to happen: yielding is enough to let it not happen, and waiting
            // on a timer would only make the suite slow.
            if (expectedAsks == 0)
            {
                for (var i = 0; i < 20; i++) await Task.Yield();
                return;
            }

            for (var i = 0; i < 200 && Source.Asked.Count < expectedAsks; i++)
            {
                await Task.Yield();
                await Task.Delay(1);
            }
        }
    }

    private sealed class Surface(Fixture fixture) : IHoverSurface
    {
        public FilePositionHit? HitTestFilePosition(PointF point) =>
            fixture.Positions.TryGetValue((point.X, point.Y), out var line)
                ? new FilePositionHit(new FileLine(line), new RawColumn(0))
                : null;

        public IReadOnlyList<Diagnostic> DiagnosticsOn(FileLine line) =>
            fixture.Problems.TryGetValue(line.Value, out var items) ? items : [];
    }

    private sealed class FakeSource : IHoverSource
    {
        private readonly List<TaskCompletionSource> _held = [];

        public List<(string Path, FileLine Line)> Asked { get; } = [];

        public bool HandlesAnything { get; set; } = true;

        public HoverText? Answer { get; set; } = new("something");

        public bool Hold { get; set; }

        public bool Handles(string absolutePath) => HandlesAnything;

        public void ReleaseHeld()
        {
            Hold = false;
            foreach (var held in _held) held.TrySetResult();
            _held.Clear();
        }

        public async Task<HoverText?> HoverAsync(
            string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken ct)
        {
            Asked.Add((absolutePath, line));
            if (Hold)
            {
                // Deliberately not cancellable: a real server answers whatever it was asked, and
                // whether a late answer reaches the screen has to be the controller's decision.
                var gate = new TaskCompletionSource();
                _held.Add(gate);
                await gate.Task.ConfigureAwait(false);
            }

            return Answer is null ? null : new HoverText($"line {line.Value}");
        }
    }

    private sealed class FakePresenter : IHoverPresenter
    {
        public HoverText? Showing { get; private set; }

        public void Show(object owner, HoverText hover, RectF anchorCanvas) => Showing = hover;

        public void Hide(object owner) => Showing = null;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Observable;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

public sealed class DefinitionProbeControllerTests
{
    private const string Root = "/repo";
    private const string File = "/repo/src/main.rs";

    private static readonly InputModifiers Command = InputModifiers.Super;

    // Asked at the symbol's own first column rather than wherever in it the pointer landed: the
    // caret column past a word's last glyph is the whitespace after it, which servers answer
    // nothing about.
    [Fact]
    public async Task CommandClickingASymbolAsksWhereItIsDeclared()
    {
        var fx = new Fixture();

        Assert.True(fx.Click(10, 10, Command));
        await fx.Settle();

        var asked = Assert.Single(fx.Source.Asked);
        Assert.Equal(File, asked.Path);
        Assert.Equal(8, asked.Line.Value);
        Assert.Equal(2, asked.Column.Value);
    }

    // The underline already cost a round trip. Clicking what it marked spends that answer: asking
    // again buys a wait the reader can see, for the answer that is on screen as the thing they
    // clicked.
    [Fact]
    public async Task ClickingAnAlreadyMarkedSymbolAsksNothingFurther()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        Assert.True(fx.Click(10, 10, Command));

        Assert.Single(fx.Source.Asked);
        Assert.Equal(Path.Combine(Root, "src", "lib.rs"), Assert.Single(fx.Navigator.Went).Path);
    }

    // Nothing is underlined there because the answer was already nowhere. Swallowing the click as
    // well would be silent: it has to reach whatever is under it.
    [Fact]
    public async Task ClickingASymbolAlreadyKnownToGoNowhereIsLeftToWhateverIsBeneath()
    {
        var fx = new Fixture();
        fx.Source.Targets = [];
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        Assert.False(fx.Click(10, 10, Command));
        await fx.Settle();

        Assert.Single(fx.Source.Asked);
        Assert.Empty(fx.Navigator.Went);
    }

    // Pressing the modifier and clicking in one motion beats the dwell, so nothing is known yet and
    // the click has to ask on its own — the affordance is an affordance, not a precondition.
    [Fact]
    public async Task AClickFasterThanTheDwellAsksForItself()
    {
        var fx = new Fixture(dwellForever: true);
        fx.Move(10, 10);
        fx.Hold(Command);

        Assert.True(fx.Click(10, 10, Command));
        await fx.SettleUntilMoved();

        Assert.Single(fx.Source.Asked);
        Assert.Single(fx.Navigator.Went);
    }

    // Nothing on screen names a symbol there, so there is no word to ask about and the caret
    // position is all there is.
    [Fact]
    public async Task ClickingSomewhereThatIsNotASymbolAsksAtTheCaret()
    {
        var fx = new Fixture();
        fx.Identifiers.Clear();

        Assert.True(fx.Click(10, 10, Command));
        await fx.Settle();

        Assert.Equal(4, Assert.Single(fx.Source.Asked).Column.Value);
    }

    [Fact]
    public async Task APlainClickAsksNothing()
    {
        var fx = new Fixture();

        Assert.False(fx.Click(10, 10, InputModifiers.None));
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
    }

    [Fact]
    public async Task ClickingSomethingThatIsNotALineOfTheFileAsksNothing()
    {
        var fx = new Fixture();

        Assert.False(fx.Click(999, 999, Command));
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
    }

    [Fact]
    public async Task AServerThatCannotAnswerIsNeverAsked()
    {
        var fx = new Fixture();
        fx.Source.Answers = false;

        Assert.False(fx.Click(10, 10, Command));
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
        Assert.Empty(fx.Navigator.Went);
    }

    [Fact]
    public async Task NoFileOnScreenMeansNoQuestion()
    {
        var fx = new Fixture();
        fx.Document = null;

        Assert.False(fx.Click(10, 10, Command));
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
    }

    [Fact]
    public async Task ADeclarationInTheRepositoryIsJumpedToOnItsOwnLine()
    {
        var fx = new Fixture();
        fx.Source.Targets = [new DefinitionTarget.InRepo("src/lib.rs", At(41))];

        fx.Click(10, 10, Command);
        await fx.SettleUntilMoved();

        var (path, line) = Assert.Single(fx.Navigator.Went);
        Assert.Equal(Path.Combine(Root, "src", "lib.rs"), path);
        Assert.Equal(42, line);
    }

    [Fact]
    public async Task ADeclarationOutsideTheRepositoryIsJumpedToByItsWholePath()
    {
        var fx = new Fixture();
        fx.Source.Targets = [new DefinitionTarget.OutsideRepo("/usr/include/stdio.h", At(0))];

        fx.Click(10, 10, Command);
        await fx.SettleUntilMoved();

        var (path, line) = Assert.Single(fx.Navigator.Went);
        Assert.Equal("/usr/include/stdio.h", path);
        Assert.Equal(1, line);
    }

    [Fact]
    public async Task TheFirstOfSeveralAnswersIsTheOneJumpedTo()
    {
        var fx = new Fixture();
        fx.Source.Targets =
        [
            new DefinitionTarget.InRepo("src/first.rs", At(2)),
            new DefinitionTarget.InRepo("src/second.rs", At(5)),
        ];

        fx.Click(10, 10, Command);
        await fx.SettleUntilMoved();

        Assert.Equal(Path.Combine(Root, "src", "first.rs"), Assert.Single(fx.Navigator.Went).Path);
    }

    [Fact]
    public async Task ASymbolDeclaredNowhereMovesNobody()
    {
        var fx = new Fixture();
        fx.Source.Targets = [];

        fx.Click(10, 10, Command);
        await fx.Settle();

        Assert.Empty(fx.Navigator.Went);
    }

    [Fact]
    public async Task TheJumpItselfHappensOnTheUiThread()
    {
        var fx = new Fixture(queued: true);

        fx.Click(10, 10, Command);
        await fx.Settle();

        Assert.Empty(fx.Navigator.Went);
        fx.Queue.Drain();
        Assert.Single(fx.Navigator.Went);
    }

    [Fact]
    public async Task AnAnswerAboutAFileTheReaderHasLeftIsDropped()
    {
        var fx = new Fixture();
        fx.Source.Hold = true;

        fx.Click(10, 10, Command);
        await fx.Settle();
        fx.Document = (Root, "/repo/src/other.rs");
        fx.Source.ReleaseHeld();
        await Task.Delay(100);

        Assert.Empty(fx.Navigator.Went);
    }

    [Fact]
    public async Task F12OverASymbolAsksTheSameQuestionAsACommandClick()
    {
        var fx = new Fixture();
        fx.Move(10, 10);

        Assert.True(fx.Key(KeyboardKey.F12, InputModifiers.None));
        await fx.SettleUntilMoved();

        Assert.Equal(8, Assert.Single(fx.Source.Asked).Line.Value);
        Assert.Single(fx.Navigator.Went);

    }

    [Fact]
    public async Task F12WithThePointerOverNothingAsksNothing()
    {
        var fx = new Fixture();
        fx.Move(999, 999);

        Assert.False(fx.Key(KeyboardKey.F12, InputModifiers.None));
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
    }

    [Fact]
    public void CommandLeftBracketGoesBack()
    {
        var fx = new Fixture();

        Assert.True(fx.Key(KeyboardKey.LeftBracket, Command));

        Assert.Equal(1, fx.Navigator.Backs);
    }

    [Fact]
    public void LeftBracketWithoutTheCommandModifierDoesNotGoBack()
    {
        var fx = new Fixture();

        Assert.False(fx.Key(KeyboardKey.LeftBracket, InputModifiers.None));

        Assert.Equal(0, fx.Navigator.Backs);
    }

    [Fact]
    public async Task HoldingTheCommandModifierOverASymbolTheServerCanReachMarksIt()
    {
        var fx = new Fixture();
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Equal(Alpha, fx.Surface.Link);
    }

    // The underline is a promise that the click will go somewhere. A symbol the server cannot
    // place is asked about and then left alone.
    [Fact]
    public async Task ASymbolTheServerCannotPlaceIsNeverMarked()
    {
        var fx = new Fixture();
        fx.Source.Targets = [];
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Single(fx.Source.Asked);
        Assert.Null(fx.Surface.Link);
    }

    // A server that reports the span it resolved knows better than the word scan does — it is the
    // one that knows a qualified name is one symbol.
    [Fact]
    public async Task TheServersOwnSpanIsWhatGetsMarked()
    {
        var fx = new Fixture();
        fx.Source.Origin = Range(line: 7, from: 0, to: 7);
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Equal(new FileSpan(new FileLine(8), new RawColumn(0), new RawColumn(7)), fx.Surface.Link);
    }

    [Theory]
    [MemberData(nameof(UnusableOrigins))]
    public async Task ASpanTheServerCouldNotHaveMeantFallsBackToTheWordOnScreen(OptionalRange origin)
    {
        var fx = new Fixture();
        fx.Source.Origin = origin;
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Equal(Alpha, fx.Surface.Link);
    }

    public static TheoryData<OptionalRange> UnusableOrigins() =>
    [
        // A symbol sits on one line; a range crossing two is not one.
        OptionalRange.Of(new LspRange(
            new LspPosition(new LspLine(7), new LspCharacter(2)),
            new LspPosition(new LspLine(8), new LspCharacter(4)))),
        // Another line entirely.
        Range(line: 3, from: 2, to: 7),
        // No width at all.
        Range(line: 7, from: 2, to: 2),
    ];

    [Fact]
    public void ThePointerAloneAsksNothingAndMarksNothing()
    {
        var fx = new Fixture();

        fx.Move(10, 10);

        Assert.Empty(fx.Source.Asked);
        Assert.Null(fx.Surface.Link);
    }

    // The dwell is what keeps a modifier held across a line of code from asking about every word
    // it passes: only a word the pointer rests on is ever asked about.
    [Fact]
    public async Task SweepingPastSymbolsAsksAboutNoneOfThem()
    {
        var fx = new Fixture(dwellForever: true);
        fx.Move(10, 10);
        fx.Hold(Command);

        fx.Move(20, 10);
        fx.Move(10, 10);
        await fx.Settle();

        Assert.Empty(fx.Source.Asked);
        Assert.Null(fx.Surface.Link);
    }

    [Fact]
    public async Task ReleasingTheModifierClearsTheMark()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Hold(InputModifiers.None);

        Assert.Null(fx.Surface.Link);
    }

    [Fact]
    public async Task MovingOffTheSymbolClearsTheMark()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Move(11, 11);

        Assert.Null(fx.Surface.Link);
    }

    [Fact]
    public async Task ThePointerLeavingTheFileClearsTheMark()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Exit();

        Assert.Null(fx.Surface.Link);
    }

    // Reading a line moves the pointer back and forth between the same few symbols. The answer for
    // the last word asked about is kept, so going back to it costs nothing and shows instantly.
    [Fact]
    public async Task ComingBackToTheSameSymbolDoesNotAskAgain()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Move(11, 11);
        fx.Move(10, 10);

        Assert.Single(fx.Source.Asked);
        Assert.Equal(Alpha, fx.Surface.Link);
    }

    [Fact]
    public async Task ADifferentSymbolIsAskedAboutOnItsOwn()
    {
        var fx = new Fixture();
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Move(20, 10);
        await fx.Settle();

        Assert.Equal(2, fx.Source.Asked.Count);
        Assert.Equal(Beta, fx.Surface.Link);
    }

    [Fact]
    public async Task AServerThatCannotAnswerAboutTheFileMarksNothing()
    {
        var fx = new Fixture();
        fx.Source.Answers = false;
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Empty(fx.Source.Asked);
        Assert.Null(fx.Surface.Link);
    }

    [Fact]
    public async Task NoFileOnScreenMarksNothing()
    {
        var fx = new Fixture();
        fx.Document = null;
        fx.Move(10, 10);

        await fx.HoldAndSettle(Command);

        Assert.Null(fx.Surface.Link);
    }

    // Every other modifier is somebody else's chord — Shift extends a text selection over the same
    // pixels.
    [Fact]
    public async Task AnotherModifierMarksNothing()
    {
        var fx = new Fixture();
        fx.Move(10, 10);

        await fx.HoldAndSettle(InputModifiers.Shift);

        Assert.Empty(fx.Source.Asked);
        Assert.Null(fx.Surface.Link);
    }

    // The answer is late by definition, and the reader has moved on. Nothing marks a word the
    // pointer has already left.
    [Fact]
    public async Task AnAnswerAboutASymbolThePointerHasLeftMarksNothing()
    {
        var fx = new Fixture();
        fx.Source.Hold = true;
        fx.Move(10, 10);
        await fx.HoldAndSettle(Command);

        fx.Move(11, 11);
        fx.Source.ReleaseHeld();
        await fx.Settle();

        Assert.Null(fx.Surface.Link);
    }

    private static readonly FileSpan Alpha =
        new(new FileLine(8), new RawColumn(2), new RawColumn(7));

    private static readonly FileSpan Beta =
        new(new FileLine(8), new RawColumn(10), new RawColumn(14));

    private static LspPosition At(int zeroBasedLine) =>
        new(new LspLine(zeroBasedLine), new LspCharacter(0));

    private static OptionalRange Range(int line, int from, int to) =>
        OptionalRange.Of(new LspRange(
            new LspPosition(new LspLine(line), new LspCharacter(from)),
            new LspPosition(new LspLine(line), new LspCharacter(to))));

    private sealed class Fixture
    {
        public Fixture(bool queued = false, bool dwellForever = false)
        {
            Positions[(10, 10)] = (8, 4);
            Identifiers[(10, 10)] = Alpha;
            Positions[(20, 10)] = (8, 12);
            Identifiers[(20, 10)] = Beta;
            Surface = new FakeSurface(this);
            Controller = new DefinitionProbeController(
                Surface,
                Source,
                Navigator,
                queued ? Queue : new ImmediateDispatcher(),
                () => Document,
                () => Held,
                dwellForever
                    ? (_, token) => Task.Delay(Timeout.Infinite, token)
                    : (_, _) => Task.CompletedTask);
        }

        public Dictionary<(float X, float Y), (int Line, int Column)> Positions { get; } = new();

        public Dictionary<(float X, float Y), FileSpan> Identifiers { get; } = new();

        public FakeSurface Surface { get; }

        public InputModifiers Held { get; set; } = InputModifiers.None;

        public FakeSource Source { get; } = new();

        public FakeNavigator Navigator { get; } = new();

        public QueuedDispatcher Queue { get; } = new();

        public DefinitionProbeController Controller { get; }

        public (string Root, string Path)? Document { get; set; } = (Root, File);

        public bool Click(float x, float y, InputModifiers modifiers) =>
            Controller.ClickedAt(new PointF(x, y), modifiers);

        public void Move(float x, float y) => Controller.MovedTo(new PointF(x, y));

        public void Hold(InputModifiers modifiers)
        {
            Held = modifiers;
            Controller.RefreshLink();
        }

        public async Task HoldAndSettle(InputModifiers modifiers)
        {
            Hold(modifiers);
            await Settle();
        }

        public void Exit()
        {
            var e = new MouseExitEvent { Mouse = new Mouse(), Phase = EventPhase.Bubbling };
            Controller.OnMouseExit(ref e);
        }

        public bool Key(KeyboardKey key, InputModifiers modifiers) =>
            Controller.PressedKey(key, modifiers);

        public async Task Settle()
        {
            for (var i = 0; i < 50; i++)
            {
                await Task.Yield();
                await Task.Delay(1);
            }
        }

        public async Task SettleUntilMoved()
        {
            for (var i = 0; i < 500 && Navigator.Went.Count == 0; i++)
            {
                await Task.Yield();
                await Task.Delay(1);
            }
        }
    }

    private sealed class FakeSurface(Fixture fixture) : IDefinitionSurface
    {
        public List<FileSpan?> Shown { get; } = [];

        public FileSpan? Link => Shown.Count == 0 ? null : Shown[^1];

        public FilePositionHit? HitTestFilePosition(PointF point) =>
            fixture.Positions.TryGetValue((point.X, point.Y), out var at)
                ? new FilePositionHit(new FileLine(at.Line), new RawColumn(at.Column))
                : null;

        public FileSpan? HitTestIdentifier(PointF point) =>
            fixture.Identifiers.TryGetValue((point.X, point.Y), out var span) ? span : null;

        public void ShowDefinitionLink(FileSpan? link) => Shown.Add(link);
    }

    private sealed class FakeSource : IDefinitionSource
    {
        private readonly List<TaskCompletionSource> _held = [];

        public List<(string Path, FileLine Line, RawColumn Column)> Asked { get; } = [];

        public bool Answers { get; set; } = true;

        public bool Hold { get; set; }

        public IReadOnlyList<DefinitionTarget> Targets { get; set; } =
            [new DefinitionTarget.InRepo("src/lib.rs", new LspPosition(new LspLine(3), new LspCharacter(0)))];

        public OptionalRange Origin { get; set; } = OptionalRange.Absent;

        public bool CanDefine(string absolutePath) => Answers;

        public void ReleaseHeld()
        {
            Hold = false;
            foreach (var held in _held) held.TrySetResult();
            _held.Clear();
        }

        public async Task<DefinitionReply> DefineAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct)
        {
            Asked.Add((absolutePath, line, column));
            if (Hold)
            {
                var gate = new TaskCompletionSource();
                _held.Add(gate);
                await gate.Task.ConfigureAwait(false);
            }

            return new DefinitionReply(Targets, Origin);
        }
    }

    private sealed class FakeNavigator : IFileNavigator
    {
        public List<(string Path, int Line)> Went { get; } = [];

        public int Backs { get; private set; }

        public void NavigateTo(string absolutePath, int line) => Went.Add((absolutePath, line));

        public void GoBack() => Backs++;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

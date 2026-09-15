using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;
using static GitBench.Tests.WalkthroughSteps;

namespace GitBench.Tests;

/// <summary>
/// The walkthrough rail mounted headlessly: it appears with the first step, its keys route through
/// the key map and stay out of the Ask field, a question carries the reviewer's selection, and a
/// trailing sidebar mirrors under RTL.
/// </summary>
public sealed class ReviewWalkthroughRailTests : IDisposable
{
    private const int Width = 800;
    private const int Height = 600;

    private sealed class FakeClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private readonly RecordingPresentation _presentation = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ReviewWalkthroughStore _store;
    private readonly State<bool> _suspended = new(false);
    private readonly KeyMap _keys = new();
    private readonly GuiTestHarness _harness;

    public ReviewWalkthroughRailTests()
    {
        _store = new ReviewWalkthroughStore(_presentation, _dispatcher, new ManualTimeProvider());
        _harness = GuiTestHarness.Create(
            ctx => new Box
            {
                Width = Width,
                Height = Height,
                Children =
                [
                    new BorderLayout
                    {
                        Center = new Box(),
                        East = new Show
                        {
                            When = _store.IsVisible,
                            Then = () => new ResizableSidebar
                            {
                                Edge = SidebarEdge.Trailing,
                                Content = new ReviewWalkthroughRail { Model = _store },
                                InitialWidth = 320f,
                            },
                        },
                    },
                ],
            }.WithController(ctx.Require<InputSystem>(), () => new WalkthroughKeyController(_store, _keys, _suspended)).BuildView(ctx),
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(_dispatcher);
                ctx.AddService<IKeyMap>(_keys);
            });
        // Keys reach the window-level controller along the hover path, as they do in the app.
        _harness.MoveTo(20, 20);
    }

    public void Dispose()
    {
        _harness.Dispose();
        _store.Dispose();
    }

    private void Show(params WalkthroughStep[] steps)
    {
        _store.Show(Agent, steps);
        _harness.Layout();
    }

    [Fact]
    public void TheRail_IsAbsentWhileIdle_AndAppearsWithTheFirstStep()
    {
        Assert.Null(_harness.Root.FindById(ReviewWalkthroughRail.RailId));

        Show(Step("The entry point"), Step("The handler"));

        Assert.NotNull(_harness.Root.FindById(ReviewWalkthroughRail.RailId));
        var canvas = _harness.Render();
        Assert.True(HasText(canvas, "Step 1 of 2"));
        Assert.True(HasText(canvas, "The entry point"));
        Assert.True(HasText(canvas, "Terminal agent"));
    }

    [Fact]
    public void TheRail_ShowsTheNarratorAtWork_FromTheAsking_UntilTheFirstStep()
    {
        _store.Begin(AssistantNarrator);
        _harness.Layout();

        Assert.NotNull(_harness.Root.FindById(ReviewWalkthroughRail.RailId));
        Assert.NotNull(_harness.Root.FindById(WalkthroughPreparingCard.CardId));
        Assert.Null(_harness.Root.FindById(WalkthroughRailFooter.NextId));
        var canvas = _harness.Render();
        Assert.True(HasText(canvas, "Assistant is reading the change…"));
        Assert.True(HasText(canvas, "Assistant is working…"));

        _store.AppendNarration("Let me look.");
        _harness.Layout();
        Assert.True(HasText(_harness.Render(), "Let me look."));

        _store.Show(AssistantNarrator, [Step("The entry point")]);
        _harness.Layout();

        Assert.Null(_harness.Root.FindById(WalkthroughPreparingCard.CardId));
        Assert.NotNull(_harness.Root.FindById(WalkthroughRailFooter.NextId));
        Assert.True(HasText(_harness.Render(), "The entry point"));
    }

    [Fact]
    public void ANarratorThatGaveUpBeforeTheFirstStep_LeavesItsWordsUp_AndOffersClear()
    {
        _store.Begin(AssistantNarrator);
        _store.ReportFailure("API key is invalid.");
        _store.MarkNarratorWaiting();
        _harness.Layout();

        var canvas = _harness.Render();
        Assert.True(HasText(canvas, "Walkthrough didn't start"));
        Assert.True(HasText(canvas, "API key is invalid."));
        Assert.False(HasText(canvas, "Assistant is reading the change…"));

        _harness.ClickOn(WalkthroughRailHeader.ClearId);
        _harness.Layout();
        Assert.Null(_harness.Root.FindById(ReviewWalkthroughRail.RailId));
    }

    [Fact]
    public void Keys_RouteThroughTheKeyMap()
    {
        Show(Step("one"), Step("two"), Step("three"));

        _harness.PressKey(KeyboardKey.Space);
        Assert.Equal(1, _store.Current.Value);

        _harness.PressKey(KeyboardKey.N);
        Assert.Equal(2, _store.Current.Value);

        _harness.PressKey(KeyboardKey.P);
        Assert.Equal(1, _store.Current.Value);
    }

    [Fact]
    public void ARebinding_IsHonoured()
    {
        _keys.Rebind(KeyCommand.WalkthroughNext, new KeyGesture(KeyboardKey.J));
        Show(Step("one"), Step("two"));

        _harness.PressKey(KeyboardKey.Space);
        Assert.Equal(0, _store.Current.Value);

        _harness.PressKey(KeyboardKey.J);
        Assert.Equal(1, _store.Current.Value);
    }

    [Fact]
    public void Keys_AreIgnoredWhileTheRailIsHidden_OrSuspended()
    {
        _harness.PressKey(KeyboardKey.Space);
        Assert.Equal(-1, _store.Current.Value);

        Show(Step("one"), Step("two"));
        _suspended.Value = true;
        _harness.PressKey(KeyboardKey.Space);
        Assert.Equal(0, _store.Current.Value);
    }

    [Fact]
    public void TheAskKey_FocusesTheField_AndTypingThereNeverSteps()
    {
        Show(Step("one"), Step("two"));

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("np n");
        _harness.PressKey(KeyboardKey.Space);

        Assert.Equal(0, _store.Current.Value);
        Assert.Equal("np n", ((GrowingDescriptionField)_harness.Get(WalkthroughAskField.InputId)).Text.ToString());
    }

    [Fact]
    public void Ask_SendsTheQuestion_WithTheSelection()
    {
        Show(Step("one"));
        var quote = new DiffSelectionQuote("a.txt", new FileLine(3), new FileLine(5), DiffQuoteSide.Added, "x = 1");
        _presentation.SelectionState.Value = quote;
        _harness.Layout();
        Assert.True(HasText(_harness.Render(), "With selection: a.txt:3-5"));
        var wait = _store.WaitAsync(CancellationToken.None);

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("why here?");
        _harness.PressKey(KeyboardKey.Enter);

        Assert.True(wait.Wait(TimeSpan.FromSeconds(5)));
        var ask = Assert.IsType<WalkthroughAction.Ask>(wait.Result);
        Assert.Equal("why here?", ask.Question);
        Assert.Same(quote, ask.Selection);
        Assert.Equal(string.Empty, ((GrowingDescriptionField)_harness.Get(WalkthroughAskField.InputId)).Text.ToString());
    }

    // The rail reads as the chat does: the question the reviewer typed under "You", the narrator's
    // answer under "Assistant", in the order they happened.
    [Fact]
    public void TheExchange_ShowsTheQuestion_AndTheAnswerUnderIt()
    {
        _store.Show(AssistantNarrator, [Step("one")]);
        _store.AppendNarration("Note this.");
        _harness.Layout();

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("why here?");
        _harness.PressKey(KeyboardKey.Enter);
        _harness.Layout();

        var asked = _harness.Render();
        Assert.True(HasText(asked, "You"));
        // Once: the exchange list is mounted by this very Add, and must not hear of it a second time.
        Assert.Equal(1, CountText(asked, "why here?"));
        Assert.True(HasText(asked, "Thinking…"));

        _store.AppendNarration("Because ");
        _store.AppendNarration("it guards the call.");
        // Streamed markdown reaches the render on the frame after it lands.
        _harness.Tick(1f / 30f);
        _harness.Layout();

        var answered = _harness.Render();
        Assert.True(HasText(answered, "Note this."));
        Assert.True(HasText(answered, "Because it guards the call."));
        Assert.Equal(1, CountText(answered, "why here?"));
        Assert.False(HasText(answered, "Thinking…"));
        Assert.Equal(3, _store.Exchange.Value.Count);
    }

    // A step that arrived with no prose has an empty exchange, so the question is what mounts the
    // list — and it must appear once, with the answer under it, not around it.
    [Fact]
    public void TheFirstQuestionOnAStep_AppearsOnce_WithTheAnswerUnderIt()
    {
        _store.Show(AssistantNarrator, [Step("one")]);
        _store.MarkNarratorWaiting();
        _harness.Layout();
        Assert.Empty(_store.Exchange.Value);
        Assert.False(_store.IsComposing.Value);

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("why here?");
        _harness.PressKey(KeyboardKey.Enter);
        _store.AppendNarration("Because.");
        _harness.Layout();

        var canvas = _harness.Render();
        Assert.Equal(1, CountText(canvas, "why here?"));
        Assert.True(HasText(canvas, "Because."));
        // Y runs bottom-up: under the question is a lower Bottom.
        Assert.True(TextBottom(canvas, "Because.") < TextBottom(canvas, "why here?"));
    }

    [Fact]
    public void AFailedTurn_IsANoticeInTheExchange()
    {
        Show(Step("one"));

        _store.ReportFailure("The model timed out.");
        _harness.Layout();

        Assert.True(HasText(_harness.Render(), "The model timed out."));
    }

    [Fact]
    public void Escape_HandsTheCaretBack_SoTheKeysStepAgain()
    {
        Show(Step("one"), Step("two"));

        _harness.PressKey(KeyboardKey.Slash);
        _harness.PressKey(KeyboardKey.Space);
        Assert.Equal(0, _store.Current.Value);

        _harness.PressKey(KeyboardKey.Escape);
        _harness.PressKey(KeyboardKey.Space);

        Assert.Equal(1, _store.Current.Value);
    }

    [Fact]
    public void TheAskButton_IsDisabledUntilThereIsAQuestion()
    {
        Show(Step("one"));
        var wait = _store.WaitAsync(CancellationToken.None);

        _harness.ClickOn(WalkthroughAskField.SendId);
        Assert.False(wait.IsCompleted);

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("   ");
        _harness.ClickOn(WalkthroughAskField.SendId);
        Assert.False(wait.IsCompleted);

        _harness.PressKey(KeyboardKey.Slash);
        _harness.Type("why?");
        _harness.ClickOn(WalkthroughAskField.SendId);

        Assert.True(wait.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("why?", Assert.IsType<WalkthroughAction.Ask>(wait.Result).Question);
    }

    [Fact]
    public void TheButtons_StepTheWalkthrough()
    {
        Show(Step("one"), Step("two"));

        _harness.ClickOn(WalkthroughRailFooter.NextId);
        Assert.Equal(1, _store.Current.Value);

        _harness.ClickOn(WalkthroughRailFooter.BackId);
        Assert.Equal(0, _store.Current.Value);
    }

    [Fact]
    public void ADisconnectedNarrator_OffersClear()
    {
        Show(Step("one"));
        Assert.Null(_harness.Root.FindById(WalkthroughRailHeader.ClearId));

        _store.MarkDisconnected();
        _harness.Layout();

        Assert.True(HasText(_harness.Render(), "Agent disconnected"));
        _harness.ClickOn(WalkthroughRailHeader.ClearId);
        _harness.Layout();
        Assert.Null(_harness.Root.FindById(ReviewWalkthroughRail.RailId));
    }

    [Fact]
    public void ASpotlightNote_IsListed_AndAClickReFocusesIt()
    {
        Show(Step("one", "a.txt", 3, Spot("a.txt", 3, 5, "the call")));
        _presentation.Calls.Clear();

        Assert.True(HasText(_harness.Render(), "the call"));
        _harness.ClickOn(WalkthroughSpotlightList.RowId(1));

        Assert.Equal(["focus a.txt:3:New"], _presentation.Calls);
    }

    [Fact]
    public void ATrailingSidebar_KeepsItsSplitterFacingTheContent_UnderBothDirections()
    {
        Show(Step("one"));
        var sidebar = (ResizableSidebarView)_harness.Root.Find(v => v is ResizableSidebarView)!;
        var splitter = sidebar.Children[1];
        Assert.Equal(sidebar.Position.Left, splitter.Position.Left, 0.5f);

        _harness.Root.IsRtl = true;
        _harness.Layout();

        Assert.Equal(sidebar.Position.Left, 0f, 0.5f);
        Assert.Equal(sidebar.Position.Right, splitter.Position.Right, 0.5f);
    }

    private static bool HasText(RecordingCanvas canvas, string text) => CountText(canvas, text) > 0;

    private static float TextBottom(RecordingCanvas canvas, string text) =>
        canvas.Texts.First(d => d.Inputs.Text.Contains(text, StringComparison.Ordinal)).Inputs.Position.Bottom;

    private static int CountText(RecordingCanvas canvas, string text)
    {
        var count = 0;
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text.Contains(text, StringComparison.Ordinal)) count++;
        return count;
    }
}

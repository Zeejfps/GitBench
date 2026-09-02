using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Dragging to select, and the one decision that matters: whether a press belongs to the selection
/// or to the program that asked for the mouse.
/// </summary>
/// <remarks>
/// Driven through the real input system with a geometry stand-in, so what is asserted is what the
/// controller does with a press, a move and a release — including the ones that land outside the
/// pane, which a drag has to keep tracking.
/// </remarks>
public class TerminalDragSelectionTests
{
    [Fact]
    public void ADrag_SelectsFromWhereItStartedToWhereItIs()
    {
        using var pane = DragPane.Create();

        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        Assert.Equal(new GridPoint(2, 0), pane.Terminal.Selection?.Start);
        Assert.Equal(new GridPoint(9, 0), pane.Terminal.Selection?.End);
    }

    [Fact]
    public void APressThatNeverMoves_IsAClickAndSelectsNothing()
    {
        using var pane = DragPane.Create();

        pane.PressAt(2, 0);
        pane.ReleaseAt(2, 0);

        Assert.Null(pane.Terminal.Selection);
    }

    [Fact]
    public void APress_ClearsWhateverWasSelectedBefore()
    {
        using var pane = DragPane.Create();
        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);
        pane.ReleaseAt(9, 0);

        pane.PressAt(1, 1);
        pane.ReleaseAt(1, 1);

        Assert.Null(pane.Terminal.Selection);
    }

    [Fact]
    public void ADragThatLeavesThePane_KeepsExtendingRatherThanStopping()
    {
        using var pane = DragPane.Create();

        pane.PressAt(2, 0);
        pane.MoveTo(9, 3);
        pane.MoveOutsideTo(20, 8);

        Assert.Equal(new GridPoint(20, 8), pane.Terminal.Selection?.End);
    }

    [Fact]
    public void AReleaseOutsideThePane_StillEndsTheDrag()
    {
        using var pane = DragPane.Create();
        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        pane.ReleaseOutside();
        var selected = pane.Terminal.Selection;
        pane.MoveOutsideTo(30, 12);

        // The gesture is over, so a pointer that keeps moving no longer drags the selection with it.
        Assert.Equal(selected, pane.Terminal.Selection);
    }

    [Fact]
    public void ADragOverTheHistory_SelectsThereRatherThanBeingRefused()
    {
        using var pane = DragPane.Create();

        pane.PressAt(0, -5);
        pane.MoveTo(4, -3);

        // The mouse-report path deliberately refuses a point over the history. A selection is the
        // reason the second, differently named, coordinate lookup exists.
        Assert.Equal(new GridPoint(0, -5), pane.Terminal.Selection?.Start);
    }

    // ---- the program's mouse, and taking it back ----

    [Fact]
    public void WhileAProgramIsTrackingTheMouse_ADragIsReportedRatherThanSelecting()
    {
        using var pane = DragPane.Tracking();

        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        Assert.Null(pane.Terminal.Selection);
        Assert.NotEmpty(pane.Terminal.Sent);
    }

    /// <remarks>
    /// Shift is the chord this pane already takes back from the shell for the wheel and the page
    /// keys, so selecting under a full-screen program is the same gesture rather than a third one.
    /// </remarks>
    [Fact]
    public void ShiftTakesTheDragBack_SoTextIsSelectableUnderAFullScreenProgram()
    {
        using var pane = DragPane.Tracking();

        pane.PressAt(2, 0, InputModifiers.Shift);
        pane.MoveTo(9, 0);

        Assert.Equal(new GridPoint(2, 0), pane.Terminal.Selection?.Start);
        Assert.Empty(pane.Terminal.Sent);
    }

    /// <remarks>
    /// The gesture is decided once, at the press. Letting go of Shift halfway through a drag would
    /// otherwise turn the rest of it into mouse reports aimed at a program that never saw it start.
    /// </remarks>
    [Fact]
    public void ReleasingShiftMidDrag_DoesNotTurnTheRestOfItIntoMouseReports()
    {
        using var pane = DragPane.Tracking();
        pane.PressAt(2, 0, InputModifiers.Shift);
        pane.MoveTo(9, 0);

        pane.MoveTo(12, 0);

        Assert.Equal(new GridPoint(12, 0), pane.Terminal.Selection?.End);
        Assert.Empty(pane.Terminal.Sent);
    }

    [Fact]
    public void OnAnExitedShell_ADragStillSelects()
    {
        using var pane = DragPane.Exited();

        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        // The screen a finished command left is what a reader most wants to copy, which is why the
        // selection path asks whether there is a screen rather than whether there is a shell.
        Assert.Equal(new GridPoint(2, 0), pane.Terminal.Selection?.Start);
    }

    /// <remarks>
    /// A drag whose release went somewhere else — a dialog opened over the pane — must not still be
    /// extending on the next move the pane sees.
    /// </remarks>
    [Fact]
    public void LosingFocusMidDrag_EndsTheDragButKeepsWhatWasSelected()
    {
        using var pane = DragPane.Create();
        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        pane.BlurTheTerminal();
        var selected = pane.Terminal.Selection;
        pane.MoveTo(20, 4);

        Assert.NotNull(selected);
        Assert.Equal(selected, pane.Terminal.Selection);
    }

    [Fact]
    public void WithNoScreenAtAll_APressStartsNothing()
    {
        using var pane = DragPane.NoScreen();

        pane.PressAt(2, 0);
        pane.MoveTo(9, 0);

        Assert.Null(pane.Terminal.Selection);
    }
}

/// <summary>
/// A controller wired to a geometry that answers in whatever cell the test names, so a drag can be
/// driven in grid coordinates rather than in pixels.
/// </summary>
internal sealed class DragPane : IDisposable
{
    readonly GuiTestHarness _harness;
    readonly ScriptedCells _cells;
    readonly TerminalInputController _controller;
    readonly Mouse _mouse = new();

    DragPane(
        SeamTerminal terminal,
        GuiTestHarness harness,
        ScriptedCells cells,
        TerminalInputController controller,
        RecordingShell shell,
        FakeClipboard clipboard,
        QueuedUiDispatcher dispatcher,
        List<ShowDialogMessage> dialogs)
    {
        Terminal = terminal;
        _harness = harness;
        _cells = cells;
        _controller = controller;
        Shell = shell;
        Clipboard = clipboard;
        _dispatcher = dispatcher;
        Dialogs = dialogs;
    }

    readonly QueuedUiDispatcher _dispatcher;

    /// <summary>Dialogs the pane has asked for, in order. Drained by <see cref="Settle"/>.</summary>
    public List<ShowDialogMessage> Dialogs { get; }

    /// <summary>Runs whatever the pane posted for the next frame, which is where a modal is asked for.</summary>
    public void Settle() => _dispatcher.Drain();

    public SeamTerminal Terminal { get; }

    public RecordingShell Shell { get; }

    public FakeClipboard Clipboard { get; }

    public GuiTestHarness Harness => _harness;

    public ScriptedCells Cells => _cells;

    public TerminalInputController Controller => _controller;

    public static DragPane Create() => Build(new SeamTerminal());

    public static DragPane Tracking() =>
        Build(new SeamTerminal { Modes = Modes(MouseTracking.ButtonEvent) });

    public static DragPane Exited() => Build(new SeamTerminal { IsAcceptingInput = false });

    public static DragPane NoScreen() =>
        Build(new SeamTerminal { IsAcceptingInput = false, HasScreen = false });

    /// <summary>A pane whose program has asked for bracketed paste.</summary>
    public static DragPane Bracketing() =>
        Build(new SeamTerminal { Modes = Modes(MouseTracking.Off, bracketedPaste: true) });

    /// <summary>A pane with no message bus or dispatcher, as a host that registered neither.</summary>
    public static DragPane Unhosted() => Build(new SeamTerminal(), hosted: false);

    static DragPane Build(SeamTerminal terminal, bool hosted = true)
    {
        var cells = new ScriptedCells();
        var shell = new RecordingShell();
        var clipboard = new FakeClipboard();
        var dispatcher = new QueuedUiDispatcher();
        var bus = new MessageBus();
        var dialogs = new List<ShowDialogMessage>();
        bus.Subscribe<ShowDialogMessage>(dialogs.Add);
        TerminalInputController? controller = null;

        var harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                var view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                controller = new TerminalInputController(
                    view, input, terminal, cells, clipboard, shell,
                    ctx, ctx.Require<ILocalizationService>());
                input.RegisterController(view, controller);
                return view;
            },
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
                if (!hosted) return;

                ctx.AddService<IMessageBus>(bus);
                ctx.AddService<IUiDispatcher>(dispatcher);
            });

        harness.Input.StealFocus(controller!);
        return new DragPane(terminal, harness, cells, controller!, shell, clipboard, dispatcher, dialogs);
    }

    const float CellSize = 10f;
    const float Origin = 200f;

    public void PressAt(int column, int row, InputModifiers modifiers = InputModifiers.None)
    {
        _cells.At(column, row);
        Send(InputState.Pressed, At(column, row), modifiers);
    }

    public void RightPressAt(int column, int row, InputModifiers modifiers = InputModifiers.None)
    {
        _cells.At(column, row);
        Send(InputState.Pressed, At(column, row), modifiers, MouseButton.Right);
    }

    public void ReleaseAt(int column, int row, InputModifiers modifiers = InputModifiers.None)
    {
        _cells.At(column, row);
        Send(InputState.Released, At(column, row), modifiers);
    }

    /// <summary>Moves the pointer without pressing, which is what establishes a hover.</summary>
    public void HoverAt(int column, int row) => Move(column, row, At(column, row));

    public void ReleaseOutside() =>
        Send(InputState.Released, new PointF(9000f, 9000f), InputModifiers.None);

    public void MoveTo(int column, int row) => Move(column, row, At(column, row));

    public void MoveOutsideTo(int column, int row) => Move(column, row, new PointF(9000f, 9000f));

    /// <summary>Where a cell sits on the pane, so a drag moves the pointer as far as it moves cells.</summary>
    static PointF At(int column, int row) =>
        new(Origin + column * CellSize, Origin - row * CellSize);

    void Move(int column, int row, PointF point)
    {
        _cells.At(column, row);
        _mouse.Point = point;
        _harness.MoveTo(point.X, point.Y);
    }

    void Send(
        InputState state,
        PointF point,
        InputModifiers modifiers,
        MouseButton? button = null)
    {
        var pressed = button ?? MouseButton.Left;

        _harness.MoveTo(point.X, point.Y);
        _mouse.Point = point;

        if (state == InputState.Pressed) _mouse.Press(pressed);
        else _mouse.Release(pressed);

        var e = new MouseButtonEvent
        {
            Mouse = _mouse,
            Button = pressed,
            State = state,
            Modifiers = modifiers,
            Phase = EventPhase.Capturing,
        };
        _harness.Input.SendMouseButtonEvent(ref e);
    }

    static TerminalModes Modes(MouseTracking tracking, bool bracketedPaste = false) => new(
        ApplicationCursorKeys: false,
        ApplicationKeypad: false,
        AutoWrap: true,
        AlternateScreen: true,
        AlternateScroll: false,
        BracketedPaste: bracketedPaste,
        FocusReporting: false,
        SynchronizedOutput: false,
        MouseTracking: tracking,
        MouseEncoding: MouseEncoding.Sgr,
        KeyboardProtocolFlags: 0,
        ModifyOtherKeys: 0);

    public void BlurTheTerminal() => _harness.Input.Blur(_controller);

    public void Dispose() => _harness.Dispose();
}

/// <summary>A geometry whose answer is whatever the test last set, in grid coordinates.</summary>
internal sealed class ScriptedCells : ITerminalCellGeometry
{
    int _column;
    int _row;

    public int Redraws { get; private set; }

    public void At(int column, int row)
    {
        _column = column;
        _row = row;
    }

    public bool TryLocate(PointF point, out int column, out int row)
    {
        column = _column;
        row = _row;

        // The live screen only, exactly as the real geometry answers: a point over the history is
        // not a cell a program can be told about.
        return _row >= 0;
    }

    public GridPoint? ClampToGrid(PointF point) => new GridPoint(_column, _row);

    public void RequestRedraw() => Redraws++;

    public TerminalLinkTarget? LinkAt(PointF point) => Link;

    /// <summary>The link every point of this pane is over, or null for none.</summary>
    public TerminalLinkTarget? Link { get; set; }

    public PointF? HoverPoint { get; private set; }

    public void SetHoverPoint(PointF? point) => HoverPoint = point;

    public TerminalLinkTarget? HoveredLink => HoverPoint is null ? null : Link;
}

using GitBench.App;
using GitBench.Terminal.Vt;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Terminal;

/// <summary>
/// The shell a terminal pane's keyboard writes to.
/// </summary>
/// <remarks>
/// <para>
/// Narrow on purpose: the controller needs somewhere to send bytes, the modes that decide how a key
/// becomes bytes, and whether there is anything on the other end at all. It does not need the
/// session, and a pane whose shell has not started yet does not have one to give it.
/// </para>
/// <para>
/// The two scrolling members are here rather than on a seam of their own because the wheel and the
/// keyboard reach them through the same controller, and because they are the members that keep
/// working after the shell has gone: reading back through what a command printed is most wanted
/// once it has finished printing it.
/// </para>
/// </remarks>
internal interface ITerminalInput
{
    /// <summary>True while there is a live shell to take input. False once it exits.</summary>
    bool IsAcceptingInput { get; }

    /// <summary>The shell's current modes, which decide how a key is encoded.</summary>
    TerminalModes Modes { get; }

    /// <summary>
    /// Sends bytes to the shell as terminal input. Does nothing when there is no shell. Returns the
    /// viewport to the live screen, because a keystroke the sender cannot see land is worse than
    /// losing their place in the history.
    /// </summary>
    void SendInput(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Moves the viewport through the history: positive back towards the oldest line, negative
    /// forwards towards the shell. Returns whether it moved.
    /// </summary>
    bool Scroll(int lines);

    /// <summary>Moves the viewport by whole screens. Returns whether it moved.</summary>
    bool ScrollPages(int pages);

    void SendMouse(ReadOnlySpan<byte> bytes);

    /// <summary>Sends pasted text to the shell, bracketed when the program has asked for it.</summary>
    void Paste(string text);

    /// <summary>
    /// Whether there is a screen to select on. Unlike <see cref="IsAcceptingInput"/> this stays true
    /// after the shell exits, because the screen it left is still readable and copyable.
    /// </summary>
    bool HasScreen { get; }

    TerminalSpan? Selection { get; }

    bool Select(GridPoint anchor, GridPoint focus, SelectionGranularity granularity);

    bool ClearSelection();

    string SelectionText();
}

/// <summary>
/// The terminal pane's keyboard: turns key presses and typed characters into the bytes a shell
/// reads, and decides which chords the application keeps instead.
/// </summary>
/// <remarks>
/// <para>
/// A focused controller is dispatched before the application's own keybindings, so Super and the
/// repo hotkeys are the only chords the terminal hands back when it has an encoding for them. That
/// list is deliberately short: a terminal that swallows Ctrl+B is broken, while an application whose
/// window shortcuts stop working over one pane is merely inconvenient. Keys it has no encoding for
/// are declined as well, which is a different rule and belongs with the delivery decision below.
/// </para>
/// <para>
/// Keys split two ways. Anything with an encoding is claimed as a command, which also suppresses the
/// character the key would have produced. Anything the text pipeline should deliver — a letter, a
/// space, an IME commit — is claimed as text instead, so the character still arrives and the layout,
/// dead keys and composition are the operating system's job rather than a table here.
/// </para>
/// <para>
/// Shift is the chord taken back from the shell, on xterm's precedent: with the page keys, and with
/// the wheel. Both move the pane's own view of the history and neither reaches the shell, and both
/// are taken whether or not there is one left to take them from — the history outlives the process
/// that printed it. Without Shift the wheel is the pane's only while no program is reading the mouse
/// and no full-screen one is up.
/// </para>
/// </remarks>
/// <summary>
/// What the pointer is doing, while a button is down.
/// </summary>
/// <remarks>
/// One value rather than a drag flag beside an anchor beside "is the program tracking the mouse":
/// selecting and reporting are alternatives, and a pair of booleans can say both at once. It also
/// makes the gesture sticky, which is the point — releasing Shift halfway through a drag must not
/// turn the rest of it into mouse reports.
/// </remarks>
internal abstract record TerminalGesture
{
    public sealed record None : TerminalGesture;

    /// <summary>A press landed on the screen; it becomes a selection if the pointer travels.</summary>
    public sealed record Armed(GridPoint Anchor, SelectionGranularity Granularity, PointF Origin) : TerminalGesture;

    public sealed record Selecting(GridPoint Anchor, SelectionGranularity Granularity) : TerminalGesture;

    /// <summary>The program asked for the mouse, so this drag is its input and not a selection.</summary>
    public sealed record Reporting : TerminalGesture;
}

internal sealed class TerminalInputController : KeyboardMouseController
{
    const int MaxUtf8BytesPerRune = 4;

    const float DragThreshold = 3f;
    const int MultiClickThresholdMs = 400;
    const float MultiClickSlopPx = 4f;

    const InputModifiers CommandLike =
        InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    const InputModifiers RelevantMask =
        InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    readonly View _view;
    readonly InputSystem _input;
    readonly ITerminalInput _terminal;
    readonly ITerminalCellGeometry _cells;
    readonly IClipboard? _clipboard;

    float _wheelRemainder;
    (int Column, int Row)? _reportedCell;

    TerminalGesture _gesture = new TerminalGesture.None();
    int _clickCount;
    int _lastClickTickMs;
    PointF _lastClickPoint;
    bool _hasLastClick;

    public TerminalInputController(
        View view,
        InputSystem input,
        ITerminalInput terminal,
        ITerminalCellGeometry cells,
        IClipboard? clipboard = null)
    {
        _view = view;
        _input = input;
        _terminal = terminal;
        _cells = cells;
        _clipboard = clipboard;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (!HasTheKeyboard()) return;

        // Before the application's reserved chords, which is the whole carve-out: on macOS the copy
        // and paste chords are Super, and every Super chord is otherwise handed straight back.
        // Copy is claimed whether or not anything is highlighted, so that the chord means one thing;
        // Ctrl+C on its own carries no Shift and is still the shell's interrupt.
        if (IsClipboardChord(e.Key, e.Modifiers, KeyboardKey.C))
        {
            Copy();
            e.Consume();
            return;
        }

        if (IsClipboardChord(e.Key, e.Modifiers, KeyboardKey.V))
        {
            Paste();
            e.Consume();
            return;
        }

        if (IsReservedForTheApplication(e.Key, e.Modifiers)) return;

        // Consumed whether or not it moved. A Shift+PageUp that reaches the top of the history and
        // then falls through would send the shell a sequence the user has spent the last four
        // presses not sending it.
        if (PagesOfHistory(e.Key, e.Modifiers) is { } pages)
        {
            _terminal.ScrollPages(pages);
            e.Consume();
            return;
        }

        if (!_terminal.IsAcceptingInput) return;

        Span<byte> sequence = stackalloc byte[TerminalKeyEncoder.MaxEncodedBytes];
        var delivery = TerminalKeyEncoder.Encode(
            TerminalKeyMap.From(e.Key),
            TerminalKeyMap.From(e.Modifiers),
            _terminal.Modes,
            sequence,
            out var written);

        switch (delivery)
        {
            case TerminalKeyDelivery.Sequence:
                _terminal.SendInput(sequence[..written]);
                e.Consume();
                break;

            case TerminalKeyDelivery.Text:
                e.ConsumeAsText();
                break;

            case TerminalKeyDelivery.None:
                if (WillTypeACharacter(e.Key, e.Modifiers)) e.ConsumeAsText();
                break;

            default:
                throw new NotSupportedException($"No rule for a {delivery} key delivery.");
        }
    }

    public override void OnTextInput(ref TextInputEvent e)
    {
        if (!HasTheKeyboard()) return;
        if (!_terminal.IsAcceptingInput) return;

        Span<byte> utf8 = stackalloc byte[MaxUtf8BytesPerRune];
        var written = e.Rune.EncodeToUtf8(utf8);

        _terminal.SendInput(utf8[..written]);
        e.Consume();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;

        if (!IsOnScreen())
        {
            _input.Blur(this);
            return;
        }

        if (e.State == InputState.Released)
        {
            var gesture = _gesture;
            _gesture = new TerminalGesture.None();

            // A press that never travelled is a click, and a click clears whatever was highlighted.
            if (gesture is TerminalGesture.Armed) Deselect();

            // The drag is this controller's, and so is the release that ends it — wherever the
            // pointer has wandered to by then.
            if (gesture is TerminalGesture.Selecting)
            {
                e.Consume();
                return;
            }
        }

        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;

        // A terminal with a screen takes the keyboard, which includes one whose shell has finished:
        // the copy chord has to reach a pane the user is reading back through. One with no screen at
        // all holds nothing, so the application's own chords survive a pointer resting over it — and
        // that is what a click on the pane's start gate reaches past.
        if (e.State == InputState.Pressed && _terminal.HasScreen) _input.StealFocus(this);

        if (e.State == InputState.Pressed && e.Button == MouseButton.Left && WantsToSelect(e.Modifiers))
        {
            BeginSelection(e.Mouse.Point);
            return;
        }

        var action = e.State == InputState.Pressed
            ? TerminalMouseAction.Press
            : TerminalMouseAction.Release;

        if (e.State == InputState.Pressed) _gesture = new TerminalGesture.Reporting();

        if (Report(ButtonOf(e.Button), action, TerminalKeyMap.From(e.Modifiers), e.Mouse.Point))
            e.Consume();
    }

    /// <remarks>
    /// The gesture ends, the selection does not. A drag whose release went somewhere else — a dialog
    /// opened over the pane, the window lost the pointer — would otherwise still be extending on the
    /// next move the pane saw, hours later. What was highlighted stays highlighted, because losing
    /// focus is not the user unselecting it and the next press clears it anyway.
    /// </remarks>
    public override void OnFocusLost() => _gesture = new TerminalGesture.None();

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (!IsOnScreen()) return;

        if (_gesture is TerminalGesture.Armed armed)
        {
            if (Travelled(e.Mouse.Point, armed.Origin) < DragThreshold) return;

            _gesture = new TerminalGesture.Selecting(armed.Anchor, armed.Granularity);
            _input.StealFocus(this);
        }

        if (_gesture is TerminalGesture.Selecting selecting)
        {
            ExtendTo(selecting, e.Mouse.Point);
            e.Consume();
            return;
        }

        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;

        Report(HeldButton(e.Mouse), TerminalMouseAction.Move, TerminalKeyModifiers.None, e.Mouse.Point);
    }

    /// <remarks>
    /// The selection is the pane's whenever the program is not tracking the mouse, and Shift takes it
    /// back when the program is — the same chord that already takes back the wheel and the page keys.
    /// </remarks>
    bool WantsToSelect(InputModifiers modifiers)
    {
        if (!_terminal.HasScreen) return false;
        if (modifiers.HasFlag(InputModifiers.Shift)) return true;

        return !_terminal.IsAcceptingInput || _terminal.Modes.MouseTracking == MouseTracking.Off;
    }

    /// <remarks>
    /// Deliberately unconsumed. The press still has to reach the pane underneath — the start gate
    /// stacked over an exited screen is a button a click has to be able to press.
    /// </remarks>
    void BeginSelection(PointF point)
    {
        if (_cells.ClampToGrid(point) is not { } anchor)
        {
            _gesture = new TerminalGesture.None();
            return;
        }

        var granularity = CountClick(point) switch
        {
            2 => SelectionGranularity.Word,
            >= 3 => SelectionGranularity.Line,
            _ => SelectionGranularity.Character,
        };

        if (granularity == SelectionGranularity.Character)
        {
            Deselect();
            _gesture = new TerminalGesture.Armed(anchor, granularity, point);
            return;
        }

        _gesture = new TerminalGesture.Selecting(anchor, granularity);
        if (_terminal.Select(anchor, anchor, granularity)) _cells.RequestRedraw();
    }

    void ExtendTo(TerminalGesture.Selecting selecting, PointF point)
    {
        if (_cells.ClampToGrid(point) is not { } focus) return;
        if (_terminal.Select(selecting.Anchor, focus, selecting.Granularity)) _cells.RequestRedraw();
    }

    void Deselect()
    {
        if (_terminal.ClearSelection()) _cells.RequestRedraw();
    }

    int CountClick(PointF point)
    {
        var now = Environment.TickCount;

        var repeats = _hasLastClick
            && now - _lastClickTickMs <= MultiClickThresholdMs
            && Travelled(point, _lastClickPoint) <= MultiClickSlopPx;

        _clickCount = repeats ? _clickCount + 1 : 1;
        _lastClickTickMs = now;
        _lastClickPoint = point;
        _hasLastClick = true;

        return _clickCount;
    }

    static float Travelled(PointF from, PointF to)
    {
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <remarks>
    /// <para>
    /// Hover, not focus: the wheel belongs to whatever is under the pointer, which is how every
    /// other scrolling surface in the window behaves and is why a focused pane does not steal the
    /// wheel from the panel someone is pointing at.
    /// </para>
    /// <para>
    /// Consumed only when something happened, so a wheel over a screen with no history behind it
    /// bubbles out to whatever scrolls around the pane instead of dead-ending on it.
    /// </para>
    /// <para>
    /// Shift takes the wheel back, which is xterm's convention and the reason the modifiers had to
    /// reach this event at all: a program that has asked for mouse events otherwise owns the wheel
    /// completely, and there would be no way to read the history behind a full-screen program
    /// without quitting it. It is the same chord as Shift with the page keys and reaches the same
    /// place, so the two do not have to be learned separately.
    /// </para>
    /// </remarks>
    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (!IsOnScreen() || !_view.Position.ContainsPoint(e.Mouse.Point)) return;

        var lines = LinesOf(e.DeltaY, IsPrecise(ref e));
        if (lines == 0) return;

        var modifiers = TerminalKeyMap.From(e.Modifiers);
        var takenBack = e.Modifiers.HasFlag(InputModifiers.Shift);

        var button = lines > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
        var notches = Math.Abs(lines);

        if (!takenBack &&
            (ReportsTheWheel(notches, button, modifiers, e.Mouse.Point) ||
             ScrollsWithCursorKeys(notches, lines)))
        {
            e.Consume();
            return;
        }

        if (_terminal.Scroll(lines)) e.Consume();
    }

    /// <summary>
    /// True when the event came from a device that reports a gesture rather than notches — a
    /// trackpad, or the momentum the system generates after one.
    /// </summary>
    static bool IsPrecise(ref MouseWheelScrolledEvent e) =>
        e.GesturePhase != ScrollPhase.None || e.IsMomentum;

    /// <remarks>
    /// One gesture is one write. A single wheel click already arrives as three notches, and trackpad
    /// momentum multiplies that, so reporting a notch at a time turned one movement of the hand into
    /// a run of syscalls carrying six bytes each — every one of them the same six bytes, since where
    /// the pointer is and what it is holding cannot change inside a single event.
    /// </remarks>
    bool ReportsTheWheel(
        int notches,
        TerminalMouseButton button,
        TerminalKeyModifiers modifiers,
        PointF point)
    {
        if (!_terminal.IsAcceptingInput) return false;
        if (notches <= 0) return false;
        if (!_cells.TryLocate(point, out var column, out var row)) return false;

        Span<byte> report = stackalloc byte[TerminalMouseEncoder.MaxEncodedBytes];
        if (!TerminalMouseEncoder.Encode(
                button,
                TerminalMouseAction.Press,
                column,
                row,
                modifiers,
                _terminal.Modes,
                report,
                out var written))
            return false;

        Repeat(report[..written], notches, _terminal.SendMouse);
        return true;
    }

    bool ScrollsWithCursorKeys(int notches, int lines)
    {
        var modes = _terminal.Modes;
        if (!_terminal.IsAcceptingInput) return false;
        if (!modes.AlternateScreen || !modes.AlternateScroll) return false;

        Span<byte> sequence = stackalloc byte[TerminalKeyEncoder.MaxEncodedBytes];
        var key = lines > 0 ? TerminalKey.Up : TerminalKey.Down;
        var delivery = TerminalKeyEncoder.Encode(
            key,
            TerminalKeyModifiers.None,
            modes,
            sequence,
            out var written);

        if (delivery != TerminalKeyDelivery.Sequence) return false;

        Repeat(sequence[..written], notches, _terminal.SendInput);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="sequence"/> <paramref name="times"/> over, in as few writes as it can.
    /// </summary>
    /// <remarks>
    /// Batched through a stack buffer while the whole run fits in one, and written one at a time
    /// when it does not — a momentum scroll can name more notches than is worth reserving stack for,
    /// and falling back costs only the syscalls it was already going to cost.
    /// </remarks>
    static void Repeat(ReadOnlySpan<byte> sequence, int times, WriteBytes write)
    {
        const int MaxBatchedBytes = 256;

        if (sequence.IsEmpty || times <= 0) return;

        if (times * sequence.Length > MaxBatchedBytes)
        {
            for (var i = 0; i < times; i++) write(sequence);
            return;
        }

        Span<byte> batch = stackalloc byte[MaxBatchedBytes];
        var at = 0;

        for (var i = 0; i < times; i++)
        {
            sequence.CopyTo(batch[at..]);
            at += sequence.Length;
        }

        write(batch[..at]);
    }

    delegate void WriteBytes(ReadOnlySpan<byte> bytes);

    bool Report(
        TerminalMouseButton button,
        TerminalMouseAction action,
        TerminalKeyModifiers modifiers,
        PointF point)
    {
        if (!_terminal.IsAcceptingInput) return false;
        if (!_cells.TryLocate(point, out var column, out var row)) return false;

        if (action == TerminalMouseAction.Move)
        {
            if (_reportedCell == (column, row)) return false;
            _reportedCell = (column, row);
        }

        Span<byte> report = stackalloc byte[TerminalMouseEncoder.MaxEncodedBytes];
        if (!TerminalMouseEncoder.Encode(
                button,
                action,
                column,
                row,
                modifiers,
                _terminal.Modes,
                report,
                out var written))
            return false;

        _terminal.SendMouse(report[..written]);
        return true;
    }

    static TerminalMouseButton ButtonOf(MouseButton button)
    {
        if (button == MouseButton.Left) return TerminalMouseButton.Left;
        if (button == MouseButton.Middle) return TerminalMouseButton.Middle;
        if (button == MouseButton.Right) return TerminalMouseButton.Right;
        return TerminalMouseButton.None;
    }

    static TerminalMouseButton HeldButton(IMouse mouse)
    {
        if (mouse.IsButtonPressed(MouseButton.Left)) return TerminalMouseButton.Left;
        if (mouse.IsButtonPressed(MouseButton.Middle)) return TerminalMouseButton.Middle;
        if (mouse.IsButtonPressed(MouseButton.Right)) return TerminalMouseButton.Right;
        return TerminalMouseButton.None;
    }

    /// <summary>
    /// Whole lines out of a wheel delta, carrying the fraction over to the next event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A trackpad reports a gesture as a stream of small fractions, and truncating each one on its
    /// own would round the whole gesture away. The carry is dropped when the direction changes so
    /// that a flick back the other way starts from a standstill rather than spending leftovers from
    /// the flick before it.
    /// </para>
    /// <para>
    /// The two devices are scaled differently because a unit means different things to them: a notch
    /// is one deliberate click and is worth a few rows, while a trackpad spends a whole gesture in
    /// small deltas and would cross the history in a flick at the same rate.
    /// </para>
    /// </remarks>
    int LinesOf(float delta, bool precise)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelRemainder)) _wheelRemainder = 0f;

        _wheelRemainder += delta * (precise ? Scrolling.PreciseWheelLines : Scrolling.WheelLines);

        var lines = (int)_wheelRemainder;
        _wheelRemainder -= lines;
        return lines;
    }

    /// <summary>
    /// The screens of history this chord asks for, or null when it is not one of them.
    /// </summary>
    static int? PagesOfHistory(KeyboardKey key, InputModifiers modifiers)
    {
        if ((modifiers & CommandLike) != 0) return null;
        if (!modifiers.HasFlag(InputModifiers.Shift)) return null;

        return key switch
        {
            KeyboardKey.PageUp => 1,
            KeyboardKey.PageDown => -1,
            _ => null,
        };
    }

    bool HasTheKeyboard()
    {
        if (!IsOnScreen())
        {
            _input.Blur(this);
            return false;
        }

        return ReferenceEquals(_input.FocusedComponent, this);
    }

    bool IsOnScreen()
    {
        for (var view = _view; view is not null; view = view.Parent)
            if (!view.IsVisible)
                return false;

        return true;
    }

    /// <summary>
    /// The clipboard chord for <paramref name="expected"/>: Cmd on macOS, Ctrl+Shift elsewhere.
    /// </summary>
    /// <remarks>
    /// Ctrl+Shift rather than Ctrl, because Ctrl+C is the interrupt and a terminal that swallowed it
    /// would be broken. Shift is already the modifier this pane takes back from the shell for the
    /// wheel and the page keys, so it is one convention rather than three.
    /// </remarks>
    static bool IsClipboardChord(KeyboardKey key, InputModifiers modifiers, KeyboardKey expected)
    {
        if (key != expected) return false;

        var held = modifiers & RelevantMask;

        return OperatingSystem.IsMacOS()
            ? held == InputModifiers.Super
            : held == (InputModifiers.Control | InputModifiers.Shift);
    }

    void Copy()
    {
        if (_clipboard is null) return;

        var text = _terminal.SelectionText();
        if (text.Length == 0) return;

        _clipboard.SetText(text);
    }

    void Paste()
    {
        if (!_terminal.IsAcceptingInput) return;
        if (_clipboard?.GetText() is not { Length: > 0 } text) return;

        _terminal.Paste(text);
    }

    static bool IsReservedForTheApplication(KeyboardKey key, InputModifiers modifiers) =>
        modifiers.HasFlag(InputModifiers.Super)
        || AppKeybindController.IsRepoHotkeyChord(key, modifiers);

    static bool WillTypeACharacter(KeyboardKey key, InputModifiers modifiers) =>
        (modifiers & CommandLike) == 0 && TerminalKeyMap.CanTypeACharacter(key);
}

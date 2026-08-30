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
/// Shift with the page keys moves the pane's own view of the history and never reaches the shell.
/// It is the chord taken back from it, on xterm's precedent, and it is taken whether or not there is
/// a shell left to take it from: the history outlives the process that printed it. The wheel is the
/// pane's only while no program is reading the mouse and no full-screen one is up.
/// </para>
/// </remarks>
internal sealed class TerminalInputController : KeyboardMouseController
{
    const int MaxUtf8BytesPerRune = 4;

    const InputModifiers CommandLike =
        InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    readonly View _view;
    readonly InputSystem _input;
    readonly ITerminalInput _terminal;
    readonly ITerminalCellGeometry _cells;

    float _wheelRemainder;
    (int Column, int Row)? _reportedCell;

    public TerminalInputController(
        View view,
        InputSystem input,
        ITerminalInput terminal,
        ITerminalCellGeometry cells)
    {
        _view = view;
        _input = input;
        _terminal = terminal;
        _cells = cells;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (!HasTheKeyboard()) return;
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

        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;

        // Only a terminal with a shell in it takes the keyboard. One that has none would hold focus
        // while declining every key, which eats the application's own chords for as long as the
        // pointer is over nothing else — and it is what a click on the pane's start gate has to be
        // able to reach past.
        if (e.State == InputState.Pressed && _terminal.IsAcceptingInput) _input.StealFocus(this);

        var action = e.State == InputState.Pressed
            ? TerminalMouseAction.Press
            : TerminalMouseAction.Release;

        if (Report(ButtonOf(e.Button), action, TerminalKeyMap.From(e.Modifiers), e.Mouse.Point))
            e.Consume();
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (!IsOnScreen() || !_view.Position.ContainsPoint(e.Mouse.Point)) return;

        Report(HeldButton(e.Mouse), TerminalMouseAction.Move, TerminalKeyModifiers.None, e.Mouse.Point);
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
    /// </remarks>
    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (!IsOnScreen() || !_view.Position.ContainsPoint(e.Mouse.Point)) return;

        var lines = LinesOf(e.DeltaY);
        if (lines == 0) return;

        var button = lines > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
        var notches = Math.Abs(lines);

        if (ReportsTheWheel(notches, button, e.Mouse.Point) || ScrollsWithCursorKeys(notches, lines))
        {
            e.Consume();
            return;
        }

        if (_terminal.Scroll(lines)) e.Consume();
    }

    bool ReportsTheWheel(int notches, TerminalMouseButton button, PointF point)
    {
        if (!_terminal.IsAcceptingInput) return false;

        var sent = false;
        for (var notch = 0; notch < notches; notch++)
            sent |= Report(button, TerminalMouseAction.Press, TerminalKeyModifiers.None, point);

        return sent;
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

        for (var notch = 0; notch < notches; notch++)
            _terminal.SendInput(sequence[..written]);

        return true;
    }

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
    /// A trackpad reports a gesture as a stream of small fractions, and truncating each one on its
    /// own would round the whole gesture away. The carry is dropped when the direction changes so
    /// that a flick back the other way starts from a standstill rather than spending leftovers from
    /// the flick before it.
    /// </remarks>
    int LinesOf(float delta)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelRemainder)) _wheelRemainder = 0f;

        _wheelRemainder += delta * Scrolling.WheelLines;

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

    static bool IsReservedForTheApplication(KeyboardKey key, InputModifiers modifiers) =>
        modifiers.HasFlag(InputModifiers.Super)
        || AppKeybindController.IsRepoHotkeyChord(key, modifiers);

    static bool WillTypeACharacter(KeyboardKey key, InputModifiers modifiers) =>
        (modifiers & CommandLike) == 0 && TerminalKeyMap.CanTypeACharacter(key);
}

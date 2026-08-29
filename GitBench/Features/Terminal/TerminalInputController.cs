using GitBench.App;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Terminal;

/// <summary>
/// The shell a terminal pane's keyboard writes to.
/// </summary>
/// <remarks>
/// Narrow on purpose: the controller needs somewhere to send bytes, the modes that decide how a key
/// becomes bytes, and whether there is anything on the other end at all. It does not need the
/// session, and a pane whose shell has not started yet does not have one to give it.
/// </remarks>
internal interface ITerminalInput
{
    /// <summary>True while there is a live shell to take input. False once it exits.</summary>
    bool IsAcceptingInput { get; }

    /// <summary>The shell's current modes, which decide how a key is encoded.</summary>
    TerminalModes Modes { get; }

    /// <summary>Sends bytes to the shell as terminal input. Does nothing when there is no shell.</summary>
    void SendInput(ReadOnlySpan<byte> bytes);
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
/// </remarks>
internal sealed class TerminalInputController : KeyboardMouseController
{
    const int MaxUtf8BytesPerRune = 4;

    const InputModifiers CommandLike =
        InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    readonly View _view;
    readonly InputSystem _input;
    readonly ITerminalInput _terminal;

    public TerminalInputController(View view, InputSystem input, ITerminalInput terminal)
    {
        _view = view;
        _input = input;
        _terminal = terminal;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (!HasTheKeyboard()) return;
        if (!_terminal.IsAcceptingInput) return;
        if (IsReservedForTheApplication(e.Key, e.Modifiers)) return;

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
        if (e.State != InputState.Pressed) return;

        if (!IsOnScreen())
        {
            _input.Blur(this);
            return;
        }

        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;

        _input.StealFocus(this);
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

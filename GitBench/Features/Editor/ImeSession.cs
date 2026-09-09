using GitBench.Features.Diff;
using ZGF.Desktop.Input;
using ZGF.Geometry;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Editor;

/// <summary>An in-flight composition: the caret it hangs off and the IME's own formatted text.</summary>
internal readonly record struct ImeComposition(DiffTextPos At, PreeditText Preedit);

/// <summary>Where the caret is, as the OS IME has to be told about it. A caret scrolled out of the
/// viewport is <see cref="At"/> with no rect, not <see cref="Nowhere"/>.</summary>
internal abstract record ImeCaret
{
    public sealed record None : ImeCaret;

    public sealed record At(DiffTextPos Pos, RectF? Rect) : ImeCaret;

    public static readonly ImeCaret Nowhere = new None();
}

/// <summary>The OS IME over one editable surface: whether it may compose at all, the preedit in
/// flight, and where the candidate window sits. The preedit lives here and never reaches the document.</summary>
internal sealed class ImeSession
{
    private abstract record State
    {
        /// <summary>Nothing is being typed into, and the OS IME is off.</summary>
        public sealed record Off : State;

        /// <summary>A caret is being typed into and the IME may compose, but nothing is in
        /// flight.</summary>
        public sealed record Ready : State;

        /// <summary>A composition in flight, pinned to the caret it started at.</summary>
        public sealed record Composing(DiffTextPos At, PreeditText Preedit) : State;

        public static readonly State Disabled = new Off();
        public static readonly State Enabled = new Ready();
    }

    private readonly InputSystem _input;

    private State _state = State.Disabled;
    private RectF? _pushed;

    public ImeSession(InputSystem input) => _input = input;

    /// <summary>What the surface draws, or null when nothing is composing.</summary>
    public ImeComposition? Composition =>
        _state is State.Composing c ? new ImeComposition(c.At, c.Preedit) : null;

    /// <summary>True while a composition is in flight, which is when the keys belong to the IME
    /// rather than to the keymap.</summary>
    public bool IsComposing => _state is State.Composing;

    /// <summary>Re-derives everything from where the caret is now. The only transition, called on
    /// every draw and every event that could have moved a caret.</summary>
    public void Sync(ImeCaret caret)
    {
        if (caret is not ImeCaret.At at)
        {
            Disable();
            return;
        }

        if (_state is State.Composing composing && composing.At != at.Pos) Abandon();

        if (_state is State.Off)
        {
            _state = State.Enabled;
            _pushed = null;
            _input.ImeHost?.SetImeEnabled(true);
        }

        if (at.Rect is not { } rect || rect == _pushed) return;
        _pushed = rect;
        _input.ImeHost?.SetImeCaretRect(rect);
    }

    /// <summary>Replaces the composition in flight. An empty preedit ends one.</summary>
    public void Update(ImeCaret caret, PreeditText preedit)
    {
        Sync(caret);
        if (_state is State.Off || caret is not ImeCaret.At at) return;
        _state = preedit.IsEmpty ? State.Enabled : new State.Composing(at.Pos, preedit);
    }

    /// <summary>Abandons any composition in flight, discarding rather than committing its text.</summary>
    public void Abandon()
    {
        if (_state is not State.Composing) return;
        _input.ImeHost?.ResetComposition();
        _state = State.Enabled;
    }

    /// <summary>Drops the preedit because the OS is committing it, without resetting the IME —
    /// a reset mid-commit would cancel the text it is handing over.</summary>
    public void Committed()
    {
        if (_state is State.Composing) _state = State.Enabled;
    }

    private void Disable()
    {
        if (_state is State.Off) return;
        Abandon();
        _state = State.Disabled;
        _pushed = null;
        _input.ImeHost?.SetImeEnabled(false);
    }
}

using System.Runtime.InteropServices;
using System.Text;
using GitBench.Features.Diff;
using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Editor;

/// <summary>What <see cref="EditorController"/> needs from the surface it drives.</summary>
internal interface IEditorSurface
{
    /// <summary>The file open for editing, or null on a read-only host, where the controller claims
    /// nothing and every key keeps bubbling.</summary>
    EditorBuffer? Editor { get; }

    /// <summary>The one selection, in the row coordinates the painter reads. Shared with the mouse
    /// gestures rather than shadowed.</summary>
    DiffSelectionModel Selection { get; }

    /// <summary>The scope this surface files its selection under.</summary>
    object? SelectionScope { get; }

    /// <summary>Where the caret is for the OS IME's sake, or nowhere on a body that is read-only or
    /// not focused.</summary>
    ImeCaret Caret { get; }

    /// <summary>How far a page moves: the rows the viewport shows, less the one kept for
    /// continuity.</summary>
    int PageRows { get; }

    /// <summary>Brings the caret into view, or does nothing when there is none.</summary>
    void RevealCaret();

    void RequestRedraw();

    /// <summary>Puts the live selection on the clipboard. False when there was nothing to copy.</summary>
    bool CopySelection();

    /// <summary>Selects the whole file. False when there is nothing to select.</summary>
    bool SelectAllText();

    /// <summary>What a paste would insert, or null when the clipboard holds no text.</summary>
    string? ClipboardText();

    /// <summary>Takes the row stream's new shape after an edit: how many rows there are and how wide
    /// their numbers are.</summary>
    void RowsChanged();

    /// <summary>Writes the open file back over itself.</summary>
    void RequestSave();
}

/// <summary>The keyboard over an editable diff body: motion, typing, deletion, indentation, the
/// clipboard and undo. Every gesture is routed through <see cref="EditSession"/>.</summary>
internal sealed class EditorController
{
    private static readonly bool IsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // The shortcut modifier: Cmd on macOS, Ctrl everywhere else.
    private static readonly InputModifiers Command = KeyGesture.Primary;

    // The word-step modifier, which on macOS is not the shortcut one.
    private static readonly InputModifiers Word =
        IsMac ? InputModifiers.Alt : InputModifiers.Control;

    // What the OS uses to reach the characters a layout hides behind a key: AltGr on Windows, which
    // arrives as Ctrl+Alt, and Option on macOS.
    private static readonly InputModifiers Compose =
        IsMac ? InputModifiers.Alt : InputModifiers.Control | InputModifiers.Alt;

    /// <summary>What a key turned out to be: nothing, a command that suppresses the character the
    /// key would have produced, or a claim that lets that character through.</summary>
    private enum Claimed
    {
        None,
        Text,
        Command,
    }

    private readonly IEditorSurface _surface;
    private readonly IKeyMap _keys;
    private readonly ImeSession _ime;

    private DiffTextPos _caret;
    private bool _hasCaret;

    public EditorController(IEditorSurface surface, InputSystem input, IKeyMap keys)
    {
        _surface = surface;
        _keys = keys;
        _ime = new ImeSession(input);
    }

    /// <summary>Whether the surface has a file open for editing.</summary>
    public bool IsEditing => _surface.Editor != null;

    /// <summary>The composition in flight, for the surface to splice into the line it draws, or null
    /// when there is none.</summary>
    public ImeComposition? Composition => _ime.Composition;

    /// <summary>Re-derives the IME's state from where the caret is now. Called from the surface's
    /// draw and from everything that can move or take away a caret without a keystroke.</summary>
    public void SyncIme() => _ime.Sync(_surface.Caret);

    /// <summary>Offers a key to the caret. Handed every key the surface's focus holder receives,
    /// and leaves alone the ones it does not claim.</summary>
    public void OnKey(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (_surface.Editor is not { } editor) return;

        var selection = _surface.Selection;
        if (!selection.IsActive) return;

        if (_ime.IsComposing)
        {
            e.ConsumeAsText();
            return;
        }

        if (!_hasCaret || _caret != selection.Focus) editor.Session.ClearGoal();

        switch (Handle(editor, selection, e.Key, e.Modifiers))
        {
            case Claimed.Text:
                e.ConsumeAsText();
                break;
            case Claimed.Command:
                e.Consume();
                break;
        }
    }

    /// <summary>Enters one character the OS committed. The only path that types.</summary>
    public void OnText(ref TextInputEvent e)
    {
        if (_surface.Editor is not { } editor) return;

        var selection = _surface.Selection;
        if (!selection.IsActive) return;

        if (Rune.IsControl(e.Rune)) return;

        _ime.Committed();

        Edit(editor, selection, editor.Session.Type(editor.SelectionOf(selection), e.Rune.ToString()));
        e.Consume();
    }

    /// <summary>Takes the composition the OS reports while a candidate is still being chosen. It is
    /// held beside the document and drawn over it, never written into it.</summary>
    public void OnComposition(ref CompositionEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        var caret = _surface.Caret;
        if (caret is not ImeCaret.At) return;

        _ime.Update(caret, e.Preedit);
        _surface.RequestRedraw();
        _surface.RevealCaret();
        SyncIme();
        e.Consume();
    }

    /// <summary>Abandons any composition in flight, discarding it rather than committing it.</summary>
    public void CancelComposition() => _ime.Abandon();

    private Claimed Handle(
        EditorBuffer editor, DiffSelectionModel selection, KeyboardKey key, InputModifiers modifiers)
    {
        if (_keys.Matches(KeyCommand.SaveFile, key, modifiers))
        {
            _surface.RequestSave();
            return Claimed.Command;
        }

        if (_keys.Matches(KeyCommand.ToggleLineComment, key, modifiers))
        {
            Edit(editor, selection, editor.Session.ToggleLineComment(editor.SelectionOf(selection)));
            return Claimed.Command;
        }

        // The editing conventions every editor shares, which are not anyone's to rebind.
        if ((modifiers & Command) != 0 && !Composes(modifiers))
        {
            var chord = Chord(editor, selection, key, (modifiers & InputModifiers.Shift) != 0);
            if (chord != Claimed.None) return chord;
        }

        if (Motion(editor, selection, key, modifiers) is { } moved)
        {
            Move(editor, selection, moved);
            return Claimed.Command;
        }

        if (Mutation(editor, selection, key, modifiers) is { } edited)
        {
            Edit(editor, selection, edited);
            return Claimed.Command;
        }

        return TypesText(key, modifiers) ? Claimed.Text : Claimed.None;
    }

    private Claimed Chord(
        EditorBuffer editor, DiffSelectionModel selection, KeyboardKey key, bool shift)
    {
        var session = editor.Session;
        switch (key)
        {
            case KeyboardKey.C:
                _surface.CopySelection();
                return Claimed.Command;

            case KeyboardKey.X:
                if (_surface.CopySelection())
                    Edit(editor, selection, session.Delete(
                        editor.SelectionOf(selection), TextUnit.Cluster, MoveDirection.Backward));
                return Claimed.Command;

            case KeyboardKey.V:
                if (_surface.ClipboardText() is { Length: > 0 } text)
                    Edit(editor, selection, session.Paste(editor.SelectionOf(selection), text));
                return Claimed.Command;

            case KeyboardKey.A:
                if (_surface.SelectAllText())
                {
                    session.ClearGoal();
                    Track(selection);
                }
                return Claimed.Command;

            case KeyboardKey.Z:
                Restore(editor, selection, shift ? session.Redo() : session.Undo());
                return Claimed.Command;

            case KeyboardKey.Y:
                Restore(editor, selection, session.Redo());
                return Claimed.Command;

            default:
                return Claimed.None;
        }
    }

    private SelectionRange? Motion(
        EditorBuffer editor, DiffSelectionModel selection, KeyboardKey key, InputModifiers modifiers)
    {
        var session = editor.Session;
        var current = editor.SelectionOf(selection);
        var intent = (modifiers & InputModifiers.Shift) != 0
            ? SelectionIntent.Extend
            : SelectionIntent.Move;
        var command = (modifiers & Command) != 0;
        var word = (modifiers & Word) != 0;

        switch (key)
        {
            case KeyboardKey.LeftArrow or KeyboardKey.RightArrow:
            {
                var direction = key == KeyboardKey.LeftArrow
                    ? MoveDirection.Backward
                    : MoveDirection.Forward;
                if (IsMac && command)
                    return session.MoveToLineEdge(
                        current,
                        direction == MoveDirection.Backward ? LineEdge.SmartStart : LineEdge.End,
                        intent);
                return session.MoveBy(
                    current, word ? TextUnit.Word : TextUnit.Cluster, direction, intent);
            }

            case KeyboardKey.UpArrow or KeyboardKey.DownArrow:
            {
                var down = key == KeyboardKey.DownArrow;
                if (IsMac && command)
                    return session.MoveToDocumentEdge(
                        current, down ? DocumentEdge.End : DocumentEdge.Start, intent);
                return session.MoveByLine(current, down ? 1 : -1, intent);
            }

            case KeyboardKey.PageUp or KeyboardKey.PageDown:
            {
                var rows = Math.Max(1, _surface.PageRows);
                return session.MoveByLine(
                    current, key == KeyboardKey.PageDown ? rows : -rows, intent);
            }

            case KeyboardKey.Home:
                return command
                    ? session.MoveToDocumentEdge(current, DocumentEdge.Start, intent)
                    : session.MoveToLineEdge(current, LineEdge.SmartStart, intent);

            case KeyboardKey.End:
                return command
                    ? session.MoveToDocumentEdge(current, DocumentEdge.End, intent)
                    : session.MoveToLineEdge(current, LineEdge.End, intent);

            default:
                return null;
        }
    }

    private static SelectionRange? Mutation(
        EditorBuffer editor, DiffSelectionModel selection, KeyboardKey key, InputModifiers modifiers)
    {
        var session = editor.Session;
        var current = editor.SelectionOf(selection);
        var unit = (modifiers & Word) != 0 ? TextUnit.Word : TextUnit.Cluster;

        switch (key)
        {
            case KeyboardKey.Backspace:
                return session.Delete(current, unit, MoveDirection.Backward);

            case KeyboardKey.Delete:
                return session.Delete(current, unit, MoveDirection.Forward);

            case KeyboardKey.Enter or KeyboardKey.NumpadEnter
                when (modifiers & (InputModifiers.Control | InputModifiers.Super)) == 0:
                return session.InsertNewline(current);

            case KeyboardKey.Tab
                when (modifiers & (InputModifiers.Control | InputModifiers.Super)) == 0:
                return (modifiers & InputModifiers.Shift) != 0
                    ? session.Outdent(current)
                    : session.Indent(current);

            default:
                return null;
        }
    }

    private static bool Composes(InputModifiers modifiers) =>
        (modifiers & Compose) == Compose && (modifiers & InputModifiers.Super) == 0;

    private static bool TypesText(KeyboardKey key, InputModifiers modifiers)
    {
        if (!Composes(modifiers)
            && (modifiers & (InputModifiers.Control | InputModifiers.Super | InputModifiers.Alt)) != 0)
            return false;

        return key
            is (>= KeyboardKey.Alpha0 and <= KeyboardKey.Z)
            or KeyboardKey.Space
            or KeyboardKey.Apostrophe
            or KeyboardKey.Comma or KeyboardKey.Period or KeyboardKey.Slash or KeyboardKey.SemiColon
            or KeyboardKey.Equals or KeyboardKey.Minus
            or KeyboardKey.LeftBracket or KeyboardKey.RightBracket
            or KeyboardKey.Backslash or KeyboardKey.GraveAccent
            or (>= KeyboardKey.Numpad0 and <= KeyboardKey.Numpad9)
            or KeyboardKey.NumpadDecimal or KeyboardKey.NumpadDivide or KeyboardKey.NumpadMultiply
            or KeyboardKey.NumpadSubtract or KeyboardKey.NumpadAdd or KeyboardKey.NumpadEquals;
    }

    private void Restore(EditorBuffer editor, DiffSelectionModel selection, SelectionRange? restored)
    {
        if (restored is { } range) Edit(editor, selection, range);
    }

    private void Move(EditorBuffer editor, DiffSelectionModel selection, SelectionRange moved)
    {
        if (editor.Write(selection, moved, _surface.SelectionScope)) _surface.RequestRedraw();
        Track(selection);
        _surface.RevealCaret();
    }

    private void Edit(EditorBuffer editor, DiffSelectionModel selection, SelectionRange edited)
    {
        _surface.RowsChanged();
        editor.Write(selection, edited, _surface.SelectionScope);
        Track(selection);
        _surface.RevealCaret();
    }

    private void Track(DiffSelectionModel selection)
    {
        _caret = selection.Focus;
        _hasCaret = true;
    }
}

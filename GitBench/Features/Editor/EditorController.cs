using System.Runtime.InteropServices;
using System.Text;
using GitBench.Features.Diff;
using GitBench.Input;
using GitBench.Lsp;
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

    /// <summary>Shows the completion list under the word it completes, or hides it when
    /// <paramref name="list"/> is null.</summary>
    void PresentCompletions(CompletionList? list);

    /// <summary>Shows documentation beside the completion list, as markdown, or hides it for null.</summary>
    void PresentCompletionDocs(string? markdown);
}

/// <summary>The keyboard over an editable diff body: motion, typing, deletion, indentation, the
/// clipboard, undo and completion. Every gesture is routed through <see cref="EditSession"/>.</summary>
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
    private readonly CompletionSession _completion = new();
    private readonly CompletionFeed? _feed;
    private readonly ParameterHints? _hints;

    // The item the docs panel is about, and what has already been fetched for this list's items.
    private CompletionItem? _docsFor;
    private readonly Dictionary<CompletionItem, string?> _docs = new(ReferenceEqualityComparer.Instance);

    private DiffTextPos _caret;
    private bool _hasCaret;

    /// <param name="feed">The language server's completions, or null where there is none to ask.</param>
    /// <param name="hints">The language server's parameter info, or null where there is none to ask.</param>
    public EditorController(
        IEditorSurface surface, InputSystem input, IKeyMap keys, CompletionFeed? feed = null, ParameterHints? hints = null)
    {
        _surface = surface;
        _keys = keys;
        _ime = new ImeSession(input);
        _feed = feed;
        _hints = hints;
    }

    /// <summary>Whether the surface has a file open for editing.</summary>
    public bool IsEditing => _surface.Editor != null;

    /// <summary>The composition in flight, for the surface to splice into the line it draws, or null
    /// when there is none.</summary>
    public ImeComposition? Composition => _ime.Composition;

    /// <summary>Re-derives the IME's state from where the caret is now. Called from the surface's
    /// draw and from everything that can move or take away a caret without a keystroke.</summary>
    public void SyncIme() => _ime.Sync(_surface.Caret);

    public bool CompletionsOpen => _completion.IsOpen;

    /// <summary>Closes the completion list, for everything that takes the caret away without a
    /// keystroke: focus leaving, another file opening.</summary>
    public void CloseCompletions()
    {
        _feed?.Cancel();
        if (!_completion.IsOpen) return;
        _completion.Close();
        PresentList();
    }

    /// <summary>Re-reads the open list against where the caret is now, after something other than a
    /// key moved it or the text under it — a click, a scroll that moved the word on screen.</summary>
    public void RefreshCompletions()
    {
        if (!_completion.IsOpen) return;
        if (_surface.Editor is not { } editor || !_surface.Selection.IsActive)
        {
            CloseCompletions();
            return;
        }

        FollowCompletions(editor, _surface.Selection);
    }

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

        if (_completion.IsOpen && SteerCompletions(editor, selection, e.Key, e.Modifiers))
        {
            e.Consume();
            return;
        }

        if (_hints is { IsOpen: true } && e.Key == KeyboardKey.Escape && e.Modifiers == InputModifiers.None)
        {
            _hints.Close();
            e.Consume();
            return;
        }

        var revision = DocumentRevision.Of(editor.Document);
        var focus = selection.Focus;
        var claimed = Handle(editor, selection, e.Key, e.Modifiers);
        if (claimed == Claimed.Command && (!revision.Describes(editor.Document) || selection.Focus != focus))
            CaretOrTextMoved(editor, selection);

        switch (claimed)
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
        CompleteTyped(editor, selection, e.Rune);
        if (_hints is not null && e.Rune.IsBmp)
            _hints.Typed(editor.Path, editor.SelectionOf(selection).Caret, (char)e.Rune.Value);
    }

    /// <summary>Closes parameter info, for everything that takes the caret away without a keystroke.</summary>
    public void CloseParameterInfo() => _hints?.Close();

    /// <summary>Asks for parameter info again against where the caret is now, after something other
    /// than a key moved it — a click.</summary>
    public void RefreshParameterInfo()
    {
        if (_hints is not { IsOpen: true } || _surface.Editor is not { } editor || !_surface.Selection.IsActive) return;
        _hints.Moved(editor.Path, editor.SelectionOf(_surface.Selection).Caret);
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

        if (_keys.Matches(KeyCommand.ShowCompletions, key, modifiers))
        {
            InvokeCompletions(editor, selection);
            return Claimed.Command;
        }

        if (_keys.Matches(KeyCommand.ParameterInfo, key, modifiers))
        {
            if (editor.SelectionOf(selection) is { IsEmpty: true } at) _hints?.Invoke(editor.Path, at.Caret);
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

    // ---- completion ----

    /// <summary>The keys an open list takes before the editor sees them. Returns whether the key
    /// was one of them.</summary>
    private bool SteerCompletions(
        EditorBuffer editor, DiffSelectionModel selection, KeyboardKey key, InputModifiers modifiers)
    {
        if (modifiers != InputModifiers.None) return false;

        // A list still waiting on its server shows nothing, so it must not take Enter from the text.
        if (_completion.Current is not { Items.Count: > 0 })
        {
            if (key == KeyboardKey.Escape) CloseCompletions();
            return false;
        }

        switch (key)
        {
            case KeyboardKey.UpArrow:
                _completion.Move(-1);
                break;
            case KeyboardKey.DownArrow:
                _completion.Move(1);
                break;
            case KeyboardKey.PageUp:
                _completion.Page(-(CompletionSession.VisibleRows - 1));
                break;
            case KeyboardKey.PageDown:
                _completion.Page(CompletionSession.VisibleRows - 1);
                break;
            case KeyboardKey.Enter or KeyboardKey.NumpadEnter:
                AcceptCompletion(editor, selection, wholeWord: false);
                return true;
            case KeyboardKey.Tab:
                AcceptCompletion(editor, selection, wholeWord: true);
                return true;
            case KeyboardKey.Escape:
                CloseCompletions();
                return true;
            default:
                return false;
        }

        PresentList();
        return true;
    }

    private void InvokeCompletions(EditorBuffer editor, DiffSelectionModel selection)
    {
        var current = editor.SelectionOf(selection);
        if (!current.IsEmpty) return;

        var serves = _feed?.Serves(editor.Path) == true;
        if (!_completion.Invoke(editor.Document, current.Caret, () => Pool(editor, current.Caret), serves))
        {
            PresentList();
            return;
        }

        // Only without a server: with one, the lone local match may not be the answer it brings.
        if (!serves && _completion.Current is { Items.Count: 1 })
        {
            AcceptCompletion(editor, selection, wholeWord: false);
            return;
        }

        if (serves) AskServer(editor, CompletionAsk.Invoked);
        PresentList();
    }

    /// <summary>After a character lands: narrows an open list, asking the server again where its
    /// last answer said typing more would bring others; or opens one — on the first letter of an
    /// identifier, or on a character the server asked to be told about.</summary>
    private void CompleteTyped(EditorBuffer editor, DiffSelectionModel selection, Rune typed)
    {
        var current = editor.SelectionOf(selection);
        if (!current.IsEmpty)
        {
            CloseCompletions();
            return;
        }

        var caret = current.Caret;
        var wasOpen = _completion.IsOpen;
        if (wasOpen)
        {
            _completion.Follow(editor.Document, caret);
            if (_completion.Current is { Server: ServerCompletionState.AnsweredIncomplete })
                AskServer(editor, CompletionAsk.Narrowing);
        }

        if (!_completion.IsOpen && InCode(editor, caret))
        {
            var serves = _feed?.Serves(editor.Path) == true;
            if (serves && typed.IsBmp && _feed!.TriggersOn(editor.Path, (char)typed.Value))
            {
                _completion.OpenForMembers(caret);
                AskServer(editor, new CompletionAsk.TypedTrigger((char)typed.Value));
            }
            else if (!wasOpen && _completion.Typed(editor.Document, caret, () => Pool(editor, caret), serves) && serves)
            {
                AskServer(editor, CompletionAsk.Invoked);
            }
        }

        PresentList();
    }

    /// <summary>Asks the server about the open list, and takes its answer into that list if it is
    /// still open at the same place when the answer comes back.</summary>
    private void AskServer(EditorBuffer editor, CompletionAsk ask)
    {
        if (_feed is null || _completion.Current is not { } list) return;

        var caret = editor.SelectionOf(_surface.Selection).Caret;
        var prefix = list.Prefix;
        _completion.Asked();
        _feed.Ask(editor.Path, caret, ask, answer =>
        {
            if (!ReferenceEquals(_surface.Editor, editor) || !_surface.Selection.IsActive) return;

            var now = editor.SelectionOf(_surface.Selection).Caret;
            _completion.Answered(list.Line, list.Start, answer.Items, answer.Incomplete, editor.Document, now);
            // Typed past the question while it was out, and the answer admits it was partial.
            if (_completion.Current is { Server: ServerCompletionState.AnsweredIncomplete } narrowed
                && narrowed.Prefix != prefix)
                AskServer(editor, CompletionAsk.Narrowing);
            PresentList();
        });
    }

    /// <summary>Shows the list as it now stands, and the documentation of whichever item is selected
    /// in it.</summary>
    private void PresentList()
    {
        var list = _completion.Current;
        _surface.PresentCompletions(list);
        ShowDocsFor(list?.SelectedItem?.Item);
    }

    /// <summary>
    /// Keeps the docs panel on the selected item. What the list carried shows at once; what has to be
    /// fetched replaces the panel when it arrives, and until then the previous item's panel stays up
    /// rather than blinking out between two rows.
    /// </summary>
    private void ShowDocsFor(CompletionItem? selected)
    {
        if (selected is null || _surface.Editor is not { } editor)
        {
            _docsFor = null;
            _docs.Clear();
            _surface.PresentCompletionDocs(null);
            return;
        }

        if (ReferenceEquals(selected, _docsFor)) return;
        _docsFor = selected;

        if (_docs.TryGetValue(selected, out var known))
        {
            _surface.PresentCompletionDocs(known);
            return;
        }

        var carried = CompletionFeed.DocsMarkdown(editor.Path, selected.Detail, selected.Documentation);
        if (selected.Resolve is not { } handle || _feed is null)
        {
            _docs[selected] = carried;
            _surface.PresentCompletionDocs(carried);
            return;
        }

        if (carried is not null) _surface.PresentCompletionDocs(carried);
        _feed.Resolve(editor.Path, handle, docs =>
        {
            var markdown = CompletionFeed.DocsMarkdown(
                editor.Path, docs?.Detail ?? selected.Detail, docs?.Documentation ?? selected.Documentation);
            _docs[selected] = markdown;
            if (ReferenceEquals(_docsFor, selected) && _completion.IsOpen) _surface.PresentCompletionDocs(markdown);
        });
    }

    private static bool InCode(EditorBuffer editor, TextPosition caret)
    {
        var line = editor.Document.Line(caret.Line);
        var options = editor.Session.Options;
        return LineContext.At(line, caret.Column.Value, options.Typing, options.LineComment) is LineContext.Code;
    }

    /// <summary>What a key that moved the caret or changed the text owes the two popups.</summary>
    private void CaretOrTextMoved(EditorBuffer editor, DiffSelectionModel selection)
    {
        FollowCompletions(editor, selection);
        var current = editor.SelectionOf(selection);
        if (current.IsEmpty) _hints?.Moved(editor.Path, current.Caret);
        else _hints?.Close();
    }

    private void FollowCompletions(EditorBuffer editor, DiffSelectionModel selection)
    {
        if (!_completion.IsOpen) return;
        var current = editor.SelectionOf(selection);
        if (current.IsEmpty) _completion.Follow(editor.Document, current.Caret);
        else _completion.Close();
        PresentList();
    }

    private void AcceptCompletion(EditorBuffer editor, DiffSelectionModel selection, bool wholeWord)
    {
        var current = editor.SelectionOf(selection);
        var accepted = _completion.Accept(editor.Document, current.Caret, wholeWord);
        _feed?.Cancel();
        PresentList();
        if (accepted is not { } edit) return;

        Edit(editor, selection, editor.Session.Complete(current, edit.Range, edit.Text, edit.Additional));
    }

    private static IReadOnlyList<CompletionItem> Pool(EditorBuffer editor, TextPosition caret) =>
        LocalCompletions.Collect(
            editor.Document, caret, editor.Rows.Outline, CompletionKeywords.For(editor.Path));

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

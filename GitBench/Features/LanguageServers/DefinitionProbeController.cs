using GitBench.App;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Input;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal sealed class DefinitionProbeController : KeyboardMouseController, IDisposable
{
    // Long enough that sweeping the pointer across a line under a held modifier asks nothing, short
    // enough that resting on a symbol marks it before the reader has decided to click. Shorter than
    // the hover card's wait: a link is an affordance, and a card is a thing to read.
    private const int DwellMs = 120;

    private readonly IDefinitionSurface _surface;
    private readonly IDefinitionSource _servers;
    private readonly IFileNavigator _navigator;

    // Back and forward walk the whole content panel, not only the files this controller jumps
    // between, so they are asked of the panel rather than of the browser.
    private readonly IContentNavigator _history;
    private readonly Func<(string Root, string Path)?> _document;
    private readonly Func<InputModifiers> _modifiers;
    private readonly IUsagesPresenter? _usages;
    private readonly IKeyMap _keys;

    // Two slots: the link probe is withdrawn whenever the pointer leaves the word, a jump is not.
    private readonly ProbeSlot _jump;
    private readonly ProbeSlot _link;
    private PointF _pointer;
    private bool _pointerInside;

    // The word a probe is out for, and the one word whose answer is remembered. One entry rather
    // than a cache: reading a line moves the pointer back and forth between two or three symbols,
    // and remembering the last answered word covers that without holding anything long enough to
    // go stale under an edit nothing here would hear about.
    private (string Path, FileSpan Word)? _asking;
    private (string Path, FileSpan Word, Probed Result)? _answered;

    public DefinitionProbeController(
        IDefinitionSurface surface,
        IDefinitionSource servers,
        IFileNavigator navigator,
        IContentNavigator history,
        IUiDispatcher dispatcher,
        Func<(string Root, string Path)?> document,
        Func<InputModifiers> modifiers,
        Func<TimeSpan, CancellationToken, Task>? dwell = null,
        IUsagesPresenter? usages = null,
        IKeyMap? keys = null)
    {
        _surface = surface;
        _servers = servers;
        _navigator = navigator;
        _history = history;
        _document = document;
        _modifiers = modifiers;
        _usages = usages;
        _keys = keys ?? KeyMap.Defaults;
        _jump = new ProbeSlot(dispatcher, dwell);
        _link = new ProbeSlot(dispatcher, dwell);
    }

    public override void OnMouseMoved(ref MouseMoveEvent e) => MovedTo(e.Mouse.Point);

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        _pointerInside = true;
        RefreshLink();
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _pointerInside = false;
        RefreshLink();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        if (e.State != InputState.Pressed) return;
        if (e.Button != MouseButton.Left) return;
        if (ClickedAt(e.Mouse.Point, e.Modifiers)) e.Consume();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        // Both edges: the link appears as the modifier goes down over a symbol and has to go away
        // again as it comes up, and the pointer has not moved in between for a mouse event to say so.
        RefreshLink();
        if (e.State != InputState.Pressed) return;
        if (!_pointerInside) return;
        if (PressedKey(e.Key, e.Modifiers)) e.Consume();
    }

    internal void MovedTo(PointF point)
    {
        _pointer = point;
        _pointerInside = true;
        RefreshLink();
    }

    /// <summary>
    /// Marks the identifier under the pointer as clickable, or unmarks whatever was marked. Reads
    /// the modifiers live rather than remembering the last key event: a chord released while another
    /// window had focus is never seen being released here, and a link left behind by one would keep
    /// claiming a click the reader means as a text selection.
    /// </summary>
    /// <remarks>
    /// Only a symbol the server can actually reach is marked, which is why this ends in a request
    /// rather than in the word it found: an underline under everything the pointer crosses is an
    /// offer the click cannot keep, and a reader learns to distrust it within a line or two.
    /// </remarks>
    internal void RefreshLink()
    {
        if (WordUnderPointer() is not { } word)
        {
            StopProbing();
            _surface.ShowDefinitionLink(null);
            return;
        }

        if (_asking == word) return;
        if (_answered is { } answered && (answered.Path, answered.Word) == word)
        {
            _surface.ShowDefinitionLink(LinkOf(answered.Result));
            return;
        }

        StopProbing();
        _surface.ShowDefinitionLink(null);
        Probe(word.Path, word.Word);
    }

    private void Probe(string path, FileSpan word)
    {
        _asking = (path, word);
        _link.Ask(
            TimeSpan.FromMilliseconds(DwellMs),
            // At the word's first column, never the caret position the pointer rounds to: a
            // server asked at the column past a word answers about the whitespace there.
            token => _servers.DefineAsync(path, word.Line, word.Start, token),
            reply =>
            {
                var probed = reply.Targets.Count == 0
                    ? Probed.Nowhere
                    : new Probed.Reachable(ResolvedSpan(reply.Origin, word), reply.Targets[0]);
                _asking = null;
                _answered = (path, word, probed);
                if (WordUnderPointer() == (path, word))
                    _surface.ShowDefinitionLink(LinkOf(probed));
            });
    }

    /// <summary>
    /// What to underline: the span the server resolved when it said one, and the word on screen
    /// otherwise. A server's span is taken only when it is on the line asked about and has width —
    /// a symbol is one line, and anything else is a misunderstanding worth ignoring rather than
    /// drawing.
    /// </summary>
    private static FileSpan ResolvedSpan(OptionalRange origin, FileSpan word)
    {
        if (origin is not OptionalRange.Present present) return word;

        var (start, end) = (present.Range.Start, present.Range.End);
        if (start.Line != end.Line) return word;
        if (start.Line.ToOneBased() != word.Line.Value) return word;
        if (end.Character.Value <= start.Character.Value) return word;

        return new FileSpan(
            word.Line, new RawColumn(start.Character.Value), new RawColumn(end.Character.Value));
    }

    private (string Path, FileSpan Word)? WordUnderPointer()
    {
        if (!_pointerInside) return null;
        if (!IsCommand(_modifiers())) return null;
        if (_document() is not { } document) return null;
        if (!_servers.CanDefine(document.Path)) return null;
        if (_surface.HitTestIdentifier(_pointer) is not { } word) return null;
        return (document.Path, word);
    }

    private void StopProbing()
    {
        _link.Cancel();
        _asking = null;
    }

    internal bool ClickedAt(PointF point, InputModifiers modifiers)
    {
        MovedTo(point);
        if (!IsCommand(modifiers)) return false;
        if (_document() is not { } document) return false;

        return ProbedAt(point, document.Path) switch
        {
            // The underline was drawn from an answer this still holds. Spend it: asking the same
            // question a second time buys a wait the reader can see, for the answer already on
            // screen as the thing they clicked.
            Probed.Reachable reachable => GoTo(document.Root, reachable.Target),
            // Asked, and the answer was nowhere — which is why nothing is underlined here.
            // Consuming the click would swallow it silently on a word the reader can already see
            // is not a link, so let it through to the selection underneath.
            Probed.Unreachable => false,
            // Never asked: a click faster than the dwell, or with no modifier held before it.
            _ => Ask(point),
        };
    }

    /// <summary>What is already known about the word under a pixel, or null when that word is not
    /// the one word an answer is being held for.</summary>
    private Probed? ProbedAt(PointF point, string path) =>
        _answered is { } answered &&
        answered.Path == path &&
        _surface.HitTestIdentifier(point) is { } word &&
        answered.Word == word
            ? answered.Result
            : null;

    private bool GoTo(string repoRoot, DefinitionTarget target)
    {
        var (path, line) = Destination(repoRoot, target);
        _navigator.NavigateTo(path, line);
        return true;
    }

    internal bool PressedKey(KeyboardKey key, InputModifiers modifiers)
    {
        if (_keys.Matches(KeyCommand.FindUsages, key, modifiers)) return ShowUsages(_pointer);
        if (_keys.Matches(KeyCommand.GoToDefinition, key, modifiers)) return Ask(_pointer);
        if (_keys.Matches(KeyCommand.NavigateBack, key, modifiers))
        {
            _history.GoBack();
            return true;
        }
        if (_keys.Matches(KeyCommand.NavigateForward, key, modifiers))
        {
            _history.GoForward();
            return true;
        }
        return false;
    }

    /// <summary>Asks where the identifier under a pixel is used, at the same position a click
    /// would ask where it is declared. The answer is the presenter's to show or to keep to
    /// itself — one usage is a jump and none is nothing at all — so this only hands the question
    /// over.</summary>
    private bool ShowUsages(PointF point)
    {
        if (_usages is null) return false;
        if (PositionToAsk(point) is not { } at) return false;

        _usages.ShowUsagesOf(point, at.Line, at.Column);
        return true;
    }

    /// <summary>
    /// What a probe learned about one word: nowhere to go, or a span to underline and the
    /// declaration behind it. A sum rather than a span and a target that would have to agree about
    /// being null, because the click reads both and the pair "underlined but goes nowhere" is the
    /// one state this must never be able to hold.
    /// </summary>
    private abstract record Probed
    {
        private Probed() { }

        public static readonly Probed Nowhere = new Unreachable();

        public sealed record Unreachable : Probed;

        public sealed record Reachable(FileSpan Link, DefinitionTarget Target) : Probed;
    }

    private static FileSpan? LinkOf(Probed probed) =>
        probed is Probed.Reachable reachable ? reachable.Link : null;

    private static bool IsCommand(InputModifiers modifiers) =>
        (modifiers & KeyGesture.Primary) != 0;

    private bool Ask(PointF point)
    {
        if (_document() is not { } document) return false;
        if (!_servers.CanDefine(document.Path)) return false;
        if (PositionToAsk(point) is not { } at) return false;

        _jump.Ask(
            TimeSpan.Zero,
            token => _servers.DefineAsync(document.Path, at.Line, at.Column, token),
            reply =>
            {
                if (reply.Targets.Count == 0) return;
                if (_document() is not { } still || still.Path != document.Path) return;
                GoTo(document.Root, reply.Targets[0]);
            });

        return true;
    }

    /// <summary>Where to ask about a pixel: the first column of the identifier under it, or the
    /// caret position when the pointer is not on one. The hit-test rounds a pointer in a glyph's
    /// trailing half up to the next column, which past the last character of a word is the
    /// whitespace after it — a position most servers answer nothing about.</summary>
    private (FileLine Line, RawColumn Column)? PositionToAsk(PointF point)
    {
        if (_surface.HitTestIdentifier(point) is { } word) return (word.Line, word.Start);
        return _surface.HitTestFilePosition(point) is { } at ? (at.Line, at.Column) : null;
    }

    private static (string Path, int Line) Destination(string repoRoot, DefinitionTarget target) =>
        target switch
        {
            DefinitionTarget.InRepo inside => (
                Path.Combine(repoRoot, inside.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                inside.Position.Line.ToOneBased()),
            DefinitionTarget.OutsideRepo outside => (
                outside.AbsolutePath, outside.Position.Line.ToOneBased()),
            _ => throw new NotSupportedException($"unhandled definition target {target.GetType().Name}"),
        };

    public void Dispose()
    {
        _jump.Cancel();
        StopProbing();
        _surface.ShowDefinitionLink(null);
    }
}

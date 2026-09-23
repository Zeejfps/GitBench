using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// The pairing loop over the Files pane: a stop is found in the file as the user has it — unsaved
/// edits included — through the tree-sitter outline, and the repository's browser opens it with the
/// caret on the declaration. Switches to the session's repository first when another is active.
/// UI thread only.
/// </summary>
internal sealed class EditorPairingPresentation : IPairingPresentation, IDisposable
{
    private readonly Repo _repo;
    private readonly IRepoRegistry _repos;
    private readonly IFileBrowserStore _browsers;
    private readonly IFileTextSource _texts;
    private readonly ISymbolExtractor _extractor;
    private readonly RepoDocumentSaver _saver;
    private readonly State<EditorCaret?> _caret = new(null);
    private readonly IDisposable _following;
    private IDisposable? _caretSubscription;
    private FileBrowserViewModel? _hinted;

    public EditorPairingPresentation(
        Repo repo,
        IRepoRegistry repos,
        IFileBrowserStore browsers,
        IFileTextSource texts,
        ISymbolExtractor extractor,
        RepoDocumentSaver saver)
    {
        _repo = repo;
        _repos = repos;
        _browsers = browsers;
        _texts = texts;
        _extractor = extractor;
        _saver = saver;
        _following = browsers.Active.Subscribe(Follow);
    }

    public IReadable<EditorCaret?> Caret => _caret;

    public async Task<StopPlacement> LocateAsync(StopTarget target, CancellationToken ct)
    {
        if (Absolute(target.Path) is not { } path)
            return new StopPlacement.Missed(new StopMiss.OutsideRepository(target.Path));

        string? text;
        switch (await _texts.ReadAsync(path, ct).ConfigureAwait(false))
        {
            case CurrentText.Complete complete:
                text = complete.Text.Replace("\r\n", "\n");
                break;
            case CurrentText.CutShort:
                return new StopPlacement.Missed(new StopMiss.Unreadable(target.Path, "the file is too large to open"));
            case CurrentText.Unavailable:
                if (Directory.Exists(path))
                    return new StopPlacement.Missed(new StopMiss.Unreadable(target.Path, "it is a directory"));
                text = null;
                break;
            default:
                throw new InvalidOperationException("Unhandled file text.");
        }

        var outline = text is not null && CodeLanguages.Detect(path) is { } language
            ? await Task.Run(() => _extractor.Extract(text, language), ct).ConfigureAwait(false)
            : null;
        return StopResolver.Resolve(path, text, outline, target);
    }

    public void Reveal(StopLocation location)
    {
        switch (location)
        {
            case StopLocation.OnSymbol symbol:
                Browser()?.PlaceCaret(symbol.AbsolutePath, symbol.At);
                break;
            case StopLocation.Insertion insertion:
                Browser()?.PlaceCaret(insertion.AbsolutePath, insertion.At);
                break;
            case StopLocation.NewFile newFile:
                Browser()?.PlaceCaret(newFile.AbsolutePath, TextPosition.At(1, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown location.");
        }
    }

    public void RevealDraft(StopLocation location, StopDraft draft)
    {
        if (PathOf(location) is not { } path) return;
        switch (draft.Place)
        {
            case DraftPlace.Replace replace:
                Browser()?.PlaceCaret(path, TextPosition.At(replace.Lines.From, 0));
                break;
            case DraftPlace.InsertAfter insert:
                Browser()?.PlaceCaret(path, TextPosition.At(insert.Line.Value, int.MaxValue));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(draft), draft.Place, "Unknown draft place.");
        }
    }

    public bool ShowFile(string relativePath, int line)
    {
        if (Absolute(relativePath) is not { } path || !File.Exists(path) || Browser() is not { } browser) return false;
        browser.PlaceCaret(path, TextPosition.At(line, 0));
        return true;
    }

    public void ShowDraft(StopLocation location, StopDraft draft, Action accept)
    {
        GhostPlace place = draft.Place switch
        {
            DraftPlace.Replace replace => new GhostPlace.Replace(new FileLine(replace.Lines.From), new FileLine(replace.Lines.To)),
            DraftPlace.InsertAfter insert => new GhostPlace.Insert(insert.Line),
            _ => throw new ArgumentOutOfRangeException(nameof(draft), draft.Place, "Unknown draft place."),
        };
        if (PathOf(location) is not { } path || Browser() is not { } browser) return;
        ClearDraft();
        _hinted = browser;
        browser.ShowHints(new EditorHints(path, new EditorGhost(place, CodeLines(draft.Code)), accept));
    }

    public void ClearDraft()
    {
        _hinted?.ShowHints(null);
        _hinted = null;
    }

    public async Task<bool> TakeDraftAsync(StopLocation location)
    {
        if (PathOf(location) is not { } path || Browser() is not { } browser) return false;
        try
        {
            return await browser.TakeGhost(path).WaitAsync(TakeTimeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // The Files pane may swing into view after the stop's file is shown; past this it isn't coming.
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(3);

    private static string? PathOf(StopLocation location) => location switch
    {
        StopLocation.OnSymbol symbol => symbol.AbsolutePath,
        StopLocation.Insertion insertion => insertion.AbsolutePath,
        StopLocation.NewFile newFile => newFile.AbsolutePath,
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown location."),
    };

    private static IReadOnlyList<string> CodeLines(string code) =>
        code.Length == 0 ? [] : code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    public IReadOnlyList<string> SaveUnsaved() => _saver.SaveUnsaved(_repo.Id);

    public string? Relative(string absolutePath)
    {
        var root = PathKey.Normalize(_repo.Path);
        var full = PathKey.Normalize(absolutePath);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        var rest = full[root.Length..];
        if (rest.Length > 0 && rest[0] != Path.DirectorySeparatorChar && rest[0] != Path.AltDirectorySeparatorChar) return null;
        return rest.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
    }

    // The browser of the session's repository, made active first: a stop is somewhere to go.
    private FileBrowserViewModel? Browser()
    {
        if (_repos.Active.Value?.Id != _repo.Id) _repos.SetActive(_repo.Id);
        return _browsers.Active.Value;
    }

    private string? Absolute(string relative)
    {
        var full = PathKey.Normalize(Path.Combine(_repo.Path, relative));
        return Relative(full) is null ? null : full;
    }

    // The caret is read off whichever browser is on screen, and only while it is this repository's.
    private void Follow(FileBrowserViewModel? browser)
    {
        _caretSubscription?.Dispose();
        _caretSubscription = null;
        if (browser is null || _repos.Active.Value?.Id != _repo.Id)
        {
            _caret.Value = null;
            return;
        }

        _caretSubscription = browser.Caret.Subscribe(caret => _caret.Value = caret);
    }

    public void Dispose()
    {
        ClearDraft();
        _following.Dispose();
        _caretSubscription?.Dispose();
    }
}

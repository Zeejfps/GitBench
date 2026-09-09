using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>What a path says right now: whole text, past the size cap, or nothing to read.</summary>
internal abstract record CurrentText
{
    private CurrentText() { }

    public static readonly CurrentText Truncated = new CutShort();

    public static readonly CurrentText Nothing = new Unavailable();

    public static CurrentText Whole(string text) => new Complete(text);

    public sealed record Complete(string Text) : CurrentText;

    public sealed record CutShort : CurrentText;

    public sealed record Unavailable : CurrentText;
}

/// <summary>
/// The text a language server should be told a path holds: what the reader is looking at, which is
/// an unsaved buffer while one is open on it and the bytes on disk otherwise.
/// </summary>
internal interface IFileTextSource
{
    Task<CurrentText> ReadAsync(string absolutePath, CancellationToken cancel);

    /// <summary>Raised with an absolute path whose text has moved since it was last read.</summary>
    event Action<string>? Changed;
}

/// <summary>The bytes on disk, which is what a path with nothing unsaved against it says.</summary>
internal sealed class FilesOnDisk : IFileTextSource
{
    public static readonly FilesOnDisk Instance = new();

    private FilesOnDisk() { }

    /// <summary>Never raised.</summary>
    public event Action<string>? Changed
    {
        add { }
        remove { }
    }

    public async Task<CurrentText> ReadAsync(string absolutePath, CancellationToken cancel)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists) return CurrentText.Nothing;
            if (info.Length > FileContentLoader.MaxTextBytes) return CurrentText.Truncated;

            return CurrentText.Whole(
                await File.ReadAllTextAsync(absolutePath, cancel).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CurrentText.Nothing;
        }
    }
}

/// <summary>The unsaved buffer open on a path, and the bytes on disk for every path without one.</summary>
internal sealed class DocumentBackedText : IFileTextSource
{
    private readonly IDocumentStore _documents;
    private readonly IUiDispatcher _dispatcher;

    public DocumentBackedText(IDocumentStore documents, IUiDispatcher dispatcher)
    {
        _documents = documents;
        _dispatcher = dispatcher;
    }

    public event Action<string>? Changed
    {
        add => _documents.Edited += value;
        remove => _documents.Edited -= value;
    }

    public async Task<CurrentText> ReadAsync(string absolutePath, CancellationToken cancel)
    {
        var unsaved = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                unsaved.TrySetResult(_documents.UnsavedTextAt(absolutePath));
            }
            catch (Exception ex)
            {
                unsaved.TrySetException(ex);
            }
        });

        return await unsaved.Task.WaitAsync(cancel).ConfigureAwait(false) is { } text
            ? CurrentText.Whole(text)
            : await FilesOnDisk.Instance.ReadAsync(absolutePath, cancel).ConfigureAwait(false);
    }
}

using GitBench.Infrastructure;
using ZGF.Gui;

namespace GitBench.Features.Editor;

/// <summary>
/// Saves every file of a repository that has edits not on disk, the way Ctrl+S saves one — for a
/// caller that needs the working tree to say what the reader typed before it looks at it. UI
/// thread only.
/// </summary>
internal sealed class RepoDocumentSaver : IHostedService, IDisposable
{
    private readonly IDocumentStore _documents;
    private readonly Dictionary<string, EditorBuffer> _buffers = new(PathKey.Comparer);
    private bool _started;

    public RepoDocumentSaver(IDocumentStore documents) => _documents = documents;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _documents.Opened += Track;
        _documents.Closed += Forget;
    }

    /// <summary>Writes each unsaved file of the repository. Answers the files that could not be
    /// written, with why.</summary>
    public IReadOnlyList<string> SaveUnsaved(Guid repoId)
    {
        var failures = new List<string>();
        foreach (var path in _documents.For(repoId).Unsaved())
        {
            if (!_buffers.TryGetValue(path, out var buffer))
            {
                failures.Add($"{path}: the open document could not be found.");
                continue;
            }

            switch (DocumentWriter.Write(path, DocumentWriter.Serialize(buffer.Document, buffer.Encoding)))
            {
                case DocumentSave.Failed failed:
                    failures.Add($"{path}: {failed.Message}");
                    break;
                case DocumentSave.Saved:
                    _documents.MarkSaved(path);
                    break;
                default:
                    throw new InvalidOperationException("Unhandled save outcome.");
            }
        }

        return failures;
    }

    private void Track(EditorBuffer buffer) => _buffers[buffer.Path] = buffer;

    private void Forget(EditorBuffer buffer)
    {
        if (_buffers.TryGetValue(buffer.Path, out var held) && ReferenceEquals(held, buffer)) _buffers.Remove(buffer.Path);
    }

    public void Dispose()
    {
        if (!_started) return;
        _documents.Opened -= Track;
        _documents.Closed -= Forget;
    }
}

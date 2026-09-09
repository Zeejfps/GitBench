using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>What Ctrl+S does beyond putting the bytes down: reporting a failed write, and offering
/// to mark a saved conflicted file resolved.</summary>
internal sealed class DocumentSaves
{
    private readonly IDocumentStore _documents;
    private readonly IGitConflictOperations _conflicts;
    private readonly IRepoRegistry _repos;
    private readonly IMessageBus _bus;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _loc;

    public DocumentSaves(
        IDocumentStore documents,
        IGitConflictOperations conflicts,
        IRepoRegistry repos,
        IMessageBus bus,
        IUiDispatcher dispatcher,
        ILocalizationService loc)
    {
        _documents = documents;
        _conflicts = conflicts;
        _repos = repos;
        _bus = bus;
        _dispatcher = dispatcher;
        _loc = loc;
    }

    /// <summary>The saver a view can reach from its scope, or null where one of the pieces is
    /// absent.</summary>
    public static DocumentSaves? From(Context ctx) =>
        ctx.Get<IDocumentStore>() is { } documents
        && ctx.Get<IGitConflictOperations>() is { } conflicts
        && ctx.Get<IRepoRegistry>() is { } repos
        && ctx.Get<IMessageBus>() is { } bus
        && ctx.Get<IUiDispatcher>() is { } dispatcher
        && ctx.Get<ILocalizationService>() is { } loc
            ? new DocumentSaves(documents, conflicts, repos, bus, dispatcher, loc)
            : null;

    /// <summary>Writes the document back over its file. A failure is reported to the reader as well
    /// as returned.</summary>
    public DocumentSave Save(string path, TextDocument document, FileEncoding encoding)
    {
        var outcome = DocumentWriter.Write(path, DocumentWriter.Serialize(document, encoding));

        if (outcome is DocumentSave.Failed failed)
        {
            _bus.Broadcast(new ShowOperationErrorMessage(
                _loc.Strings.Value.EditorSaveFailedTitle, failed.Message));
            return outcome;
        }

        _documents.MarkSaved(path);

        AskAboutConflict(path);
        return outcome;
    }

    private void AskAboutConflict(string absolutePath)
    {
        if (OwnerOf(absolutePath) is not { } owner) return;
        var (repo, relative) = owner;

        Task.Run(() =>
        {
            if (!IsUnmerged(repo, relative)) return;
            _dispatcher.Post(() => Offer(repo, relative));
        });
    }

    /// <summary>The repository the saved file is in — the deepest one, so a submodule's file is not
    /// asked about against its parent.</summary>
    private (Repo Repo, string Relative)? OwnerOf(string absolutePath)
    {
        (Repo Repo, string Relative)? owner = null;
        foreach (var repo in _repos.Repos)
        {
            if (RelativePath(repo, absolutePath) is not { } relative) continue;
            if (owner is { } found && found.Relative.Length <= relative.Length) continue;
            owner = (repo, relative);
        }

        return owner;
    }

    private void Offer(Repo repo, string relativePath) =>
        _bus.Broadcast(new ShowDialogMessage(onClose => new MarkResolvedDialog
        {
            Repo = repo,
            RelativePath = relativePath,
            OnClose = onClose,
        }));

    private bool IsUnmerged(Repo repo, string relativePath)
    {
        try
        {
            foreach (var conflicted in _conflicts.GetConflictedPaths(repo))
                if (PathKey.Comparer.Equals(conflicted.Path, relativePath)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static string? RelativePath(Repo repo, string absolutePath)
    {
        var relative = Path.GetRelativePath(repo.Path, absolutePath).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null
            : relative;
    }
}

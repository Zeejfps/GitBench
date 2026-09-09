using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Gui;

namespace GitBench.Features.Editor;

/// <summary>What of a working tree an operation is about to rewrite.</summary>
internal abstract record OverwriteScope
{
    private OverwriteScope() { }

    /// <summary>Exactly these files, by absolute path.</summary>
    internal sealed record Files(Guid RepoId, IReadOnlyList<string> Paths) : OverwriteScope;

    /// <summary>Anywhere in this working tree.</summary>
    internal sealed record WorkingTree(Guid RepoId) : OverwriteScope;
}

/// <summary>Names an unsaved file for display: repo-relative while its repository is open, absolute
/// once it is not.</summary>
internal static class UnsavedFileName
{
    public static string Of(IRepoRegistry registry, UnsavedFile file)
    {
        if (registry.Repos.FirstOrDefault(r => r.Id == file.RepoId) is not { } repo) return file.Path;

        var relative = Path.GetRelativePath(PathKey.Normalize(repo.Path), file.Path).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) ? file.Path : relative;
    }
}

/// <summary>Asks before an operation rewrites files the reader has typed into and not saved.</summary>
internal interface IUnsavedEditsGuard
{
    /// <summary>Runs <paramref name="proceed"/> — synchronously when nothing unsaved is at stake,
    /// after the reader agrees when there is, and not at all if they decline.</summary>
    void Guard(OverwriteScope scope, Action proceed);
}

/// <summary>The one place an operation about to rewrite the working tree asks about unsaved edits.
/// Consent only: it closes no document and writes no file.</summary>
internal sealed class UnsavedEditsGuard(
    IDocumentStore documents, IRepoRegistry registry, IMessageBus bus) : IUnsavedEditsGuard
{
    public void Guard(OverwriteScope scope, Action proceed)
    {
        var losing = Losing(scope);
        if (losing.Count == 0)
        {
            proceed();
            return;
        }

        bus.Broadcast(new ShowDialogMessage(onClose => new UnsavedChangesDialog
        {
            Files = losing.Select(f => UnsavedFileName.Of(registry, f)).ToArray(),
            Kind = UnsavedChangesKind.Overwrite,
            OnClose = onClose,
            OnConfirm = proceed,
        }));
    }

    private IReadOnlyList<UnsavedFile> Losing(OverwriteScope scope) => scope switch
    {
        OverwriteScope.WorkingTree tree =>
            documents.Unsaved().Where(f => f.RepoId == tree.RepoId).ToArray(),
        OverwriteScope.Files files =>
            documents.Unsaved().Where(f => f.RepoId == files.RepoId && Names(files, f.Path)).ToArray(),
        _ => [],
    };

    private static bool Names(OverwriteScope.Files scope, string path)
    {
        foreach (var named in scope.Paths)
            if (PathKey.Comparer.Equals(PathKey.Normalize(named), path)) return true;
        return false;
    }
}

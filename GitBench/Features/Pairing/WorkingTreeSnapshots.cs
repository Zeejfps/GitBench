using GitBench.Features.Repos;
using GitBench.Git;

namespace GitBench.Features.Pairing;

/// <summary>A working tree as git saw it at one moment: the id of a tree object holding every
/// tracked and untracked, non-ignored file.</summary>
internal readonly record struct TreeSnapshot(string TreeId);

/// <summary>What taking or comparing a snapshot came to.</summary>
internal abstract record SnapshotResult<T>
{
    public sealed record Ok(T Value) : SnapshotResult<T>;

    public sealed record Failed(string Reason) : SnapshotResult<T>;
}

/// <summary>
/// Captures the working tree as a git tree and diffs two captures, so the diff of a stop is the
/// user's edit and nothing that was there before it. Written through a throwaway index, so the
/// user's staging area is never touched; the blobs land in the object store unreferenced, where
/// git's own collection removes them. Blocking: call off the UI thread.
/// </summary>
internal sealed class WorkingTreeSnapshots
{
    /// <summary>A diff past this many characters is cut, with a note saying so.</summary>
    public const int MaxDiffChars = 60_000;

    private readonly GitProcessRunner _git;

    public WorkingTreeSnapshots(IRepoActivityTracker activity) => _git = new GitProcessRunner(activity);

    public SnapshotResult<TreeSnapshot> Capture(string repoPath)
    {
        var indexPath = Path.Combine(Path.GetTempPath(), $"diffdino-pairing-{Guid.NewGuid():N}.index");
        try
        {
            // Starting from a copy of the real index keeps its stat cache, so `add` only rehashes
            // what changed.
            var located = _git.Run(repoPath, ["rev-parse", "--git-path", "index"], inject: false);
            if (located.Ok)
            {
                var real = located.Stdout.Trim();
                if (!Path.IsPathRooted(real)) real = Path.Combine(repoPath, real);
                if (File.Exists(real)) File.Copy(real, indexPath);
            }

            void WithIndex(System.Diagnostics.ProcessStartInfo psi) => psi.Environment["GIT_INDEX_FILE"] = indexPath;
            var added = _git.Run(repoPath, ["add", "-A", "--", "."], configure: WithIndex, inject: false);
            if (!added.Ok) return new SnapshotResult<TreeSnapshot>.Failed(added.FirstLineError("git add"));
            var written = _git.Run(repoPath, ["write-tree"], configure: WithIndex, inject: false);
            if (!written.Ok) return new SnapshotResult<TreeSnapshot>.Failed(written.FirstLineError("git write-tree"));
            return new SnapshotResult<TreeSnapshot>.Ok(new TreeSnapshot(written.Stdout.Trim()));
        }
        catch (IOException e)
        {
            return new SnapshotResult<TreeSnapshot>.Failed(e.Message);
        }
        finally
        {
            TryDelete(indexPath);
            TryDelete(indexPath + ".lock");
        }
    }

    /// <summary>The unified diff from <paramref name="from"/> to <paramref name="to"/>; empty when
    /// nothing changed.</summary>
    public SnapshotResult<string> Diff(string repoPath, TreeSnapshot from, TreeSnapshot to)
    {
        if (from == to) return new SnapshotResult<string>.Ok(string.Empty);
        var diff = _git.Run(repoPath, ["diff", "--no-color", "--no-ext-diff", "--find-renames", from.TreeId, to.TreeId], inject: false);
        if (!diff.Ok) return new SnapshotResult<string>.Failed(diff.FirstLineError("git diff"));
        var text = diff.Stdout;
        if (text.Length > MaxDiffChars)
            text = text[..MaxDiffChars] + $"\n… diff cut at {MaxDiffChars} characters.";
        return new SnapshotResult<string>.Ok(text);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

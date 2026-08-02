using GitBench.Features.Commits;

namespace GitBench.Git;

public interface IGitDiffReader
{
    // The combined net file list of a review range (base→head as one diff), for the Review window's
    // Combined mode. base/head are resolved SHAs; the list is the same FileChange shape as a commit's.
    Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha);
    DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null);
    // Full file text for one side of a diff, used by syntax highlighting's whole-file tokenize.
    // oldSide picks the "before" content (removed lines), else the "after" content (added/
    // context). Returns null when that side has no content (root commit's parent, pure add/
    // delete) or on any failure — the caller then renders that side plain.
    string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null);
    // Same addressing as GetFileText, but the blob's raw bytes — nothing is decoded as text, so
    // binary content survives. Backs the diff view's image preview. Returns null when that side
    // has no content, on any failure, or when the blob exceeds maxBytes.
    byte[]? GetFileBytes(Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null, string? baseSha = null);
}

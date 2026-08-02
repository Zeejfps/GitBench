namespace GitBench.Git;

public interface IGitRepositoryReader
{
    // Whether git tracks a repo-relative working-tree path (`git ls-files --error-unmatch`).
    // The assistant's file reads resolve through this instead of the filesystem, so an untracked
    // file — a stray .env, a scratch dump — is invisible to them.
    bool IsPathTracked(Repo repo, string relativePath);
    // Whether the ignore rules match a path, asked with `--no-index` so the answer is the rules'
    // and not "it is tracked, so no".
    bool IsPathIgnored(Repo repo, string relativePath);
    // Every repo-relative path git tracks (`git ls-files --cached`), sorted and deduplicated.
    // Backs the assistant's file search, so a path it half-remembers can be resolved to a real one.
    IReadOnlyList<string> ListTrackedFiles(Repo repo);
}

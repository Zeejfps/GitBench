using GitBench.Features.CodeIntel;
using GitBench.Features.LanguageServers;
using GitBench.Git;
using GitBench.Infrastructure;

namespace GitBench.Features.Pairing;

/// <summary>
/// Finds places in one repository's files as the user has them, unsaved edits included: a
/// declaration by name through the tree-sitter outline, and repo-relative paths kept inside the
/// repository.
/// </summary>
internal sealed class RepoFilePlaces
{
    private readonly Repo _repo;
    private readonly IFileTextSource _texts;
    private readonly ISymbolExtractor _extractor;

    public RepoFilePlaces(Repo repo, IFileTextSource texts, ISymbolExtractor extractor)
    {
        _repo = repo;
        _texts = texts;
        _extractor = extractor;
    }

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

    /// <summary>A file's lines as the user has them, or null when there is no such file or it is
    /// too large to open.</summary>
    public async Task<IReadOnlyList<string>?> ReadLinesAsync(string absolutePath, CancellationToken ct) =>
        await _texts.ReadAsync(absolutePath, ct).ConfigureAwait(false) is CurrentText.Complete complete
            ? complete.Text.Replace("\r\n", "\n").Split('\n')
            : null;

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

    /// <summary>The absolute path of a repo-relative one, or null when it leads out of the repository.</summary>
    public string? Absolute(string relative)
    {
        var full = PathKey.Normalize(Path.Combine(_repo.Path, relative));
        return Relative(full) is null ? null : full;
    }
}

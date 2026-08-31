using System.Text;

using GitBench.Features.CodeIntel;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// The real files the two highlighters are measured over, and the upstream highlight queries the
/// tree-sitter side runs. Both come off disk from the checkout the tests were built in.
/// </summary>
internal static class BenchCorpus
{
    // Both engines refuse a file above their own cap, so a corpus that mixed in tree-sitter's
    // generated multi-megabyte parser.c would compare two fallbacks. Those files are counted in
    // the guardrail section instead.
    public const int MaxCorpusBytes = 1024 * 1024;

    private static readonly string[] ExcludedSegments =
        [".git", "bin", "obj", "node_modules", "artifacts"];

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Every corpus file for a language, path-sorted so a run is reproducible.</summary>
    public static IReadOnlyList<CorpusFile> Files(CodeLanguage language, int limit)
    {
        var files = new List<CorpusFile>();

        foreach (var path in EnumerateSource())
        {
            if (CodeLanguages.Detect(path) != language) continue;

            var info = new FileInfo(path);
            if (info.Length is 0 or > MaxCorpusBytes) continue;

            files.Add(new CorpusFile(path, Path.GetRelativePath(RepoRoot, path), info.Length));
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files.Count <= limit ? files : Spread(files, limit);
    }

    /// <summary>Files a language bundles a grammar for that are too big for the timing corpus.</summary>
    public static IReadOnlyList<CorpusFile> Oversized()
    {
        var files = new List<CorpusFile>();

        foreach (var path in EnumerateSource())
        {
            if (CodeLanguages.Detect(path) is null) continue;

            var info = new FileInfo(path);
            if (info.Length <= MaxCorpusBytes) continue;

            files.Add(new CorpusFile(path, Path.GetRelativePath(RepoRoot, path), info.Length));
        }

        files.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        return files;
    }

    /// <summary>
    /// The upstream <c>highlights.scm</c> for a language, as the grammar's own repository ships it.
    /// </summary>
    /// <remarks>
    /// TypeScript and TSX are the exception the <c>tree-sitter-highlight</c> configuration also
    /// makes: their query files hold only what JavaScript's does not, so the two are concatenated.
    /// </remarks>
    public static string? Query(CodeLanguage language)
    {
        var javascript = ReadQuery("tree-sitter-javascript");

        return language switch
        {
            CodeLanguage.CSharp => ReadQuery("tree-sitter-c-sharp"),
            CodeLanguage.JavaScript => javascript,
            CodeLanguage.TypeScript or CodeLanguage.Tsx =>
                Concat(javascript, ReadQuery("tree-sitter-typescript")),
            CodeLanguage.Json => ReadQuery("tree-sitter-json"),
            CodeLanguage.Css => ReadQuery("tree-sitter-css"),
            CodeLanguage.Html => ReadQuery("tree-sitter-html"),
            CodeLanguage.Markdown => ReadQuery("tree-sitter-markdown", "tree-sitter-markdown"),
            CodeLanguage.Yaml => ReadQuery("tree-sitter-yaml"),
            CodeLanguage.Python => ReadQuery("tree-sitter-python"),
            CodeLanguage.Go => ReadQuery("tree-sitter-go"),
            CodeLanguage.Rust => ReadQuery("tree-sitter-rust"),
            CodeLanguage.Java => ReadQuery("tree-sitter-java"),
            CodeLanguage.Bash => ReadQuery("tree-sitter-bash"),
            CodeLanguage.C => ReadQuery("tree-sitter-c"),
            _ => null,
        };
    }

    private static string? Concat(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "\n" + second;

    private static string? ReadQuery(params string[] segments)
    {
        var path = Path.Combine(
            [RepoRoot, "external", "cs_tree_sitter", "native", "vendor", .. segments, "queries", "highlights.scm"]);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>An evenly spaced sample, so a cap does not silently mean "the first N directories".</summary>
    private static List<CorpusFile> Spread(List<CorpusFile> files, int limit)
    {
        var sample = new List<CorpusFile>(limit);
        for (var i = 0; i < limit; i++)
        {
            sample.Add(files[(int)((long)i * files.Count / limit)]);
        }

        return sample;
    }

    private static IEnumerable<string> EnumerateSource()
    {
        var stack = new Stack<string>();
        stack.Push(RepoRoot);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedSegments.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            foreach (var file in Directory.GetFiles(directory))
            {
                yield return file;
            }
        }
    }

    /// <remarks>
    /// The build output is not necessarily under the checkout — an <c>--artifacts-path</c> run puts
    /// it elsewhere entirely — so this file's own compile-time path is the second candidate.
    /// </remarks>
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return Above(AppContext.BaseDirectory)
            ?? Above(Path.GetDirectoryName(here) ?? "")
            ?? throw new InvalidOperationException("Could not find GitBench.sln above the test output directory.");

        static string? Above(string start)
        {
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start)) return null;

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GitBench.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            return null;
        }
    }

    /// <summary>Reads a file as UTF-8, or null where it is not text this benchmark can compare.</summary>
    public static string? ReadText(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        return new UTF8Encoding(false, false).GetString(bytes);
    }
}

internal readonly record struct CorpusFile(string Path, string RelativePath, long Bytes);

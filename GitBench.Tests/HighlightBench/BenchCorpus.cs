using System.Text;

using GitBench.Features.CodeIntel;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// The real files the two highlighters are measured over, off disk from the checkout the tests
/// were built in.
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

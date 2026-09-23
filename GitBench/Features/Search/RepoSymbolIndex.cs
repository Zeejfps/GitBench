using System.Collections.Concurrent;
using GitBench.Features.CodeIntel;
using GitBench.Infrastructure;

namespace GitBench.Features.Search;

/// <summary>How far an index has got.</summary>
internal abstract record SymbolIndexProgress
{
    private SymbolIndexProgress() { }

    /// <summary>Nothing listed yet: the repository's files are still being read.</summary>
    public sealed record Listing : SymbolIndexProgress;

    public sealed record Building(int Done, int Total) : SymbolIndexProgress;

    public sealed record Complete : SymbolIndexProgress;
}

/// <summary>One repository's symbols as far as the index has got. Immutable: a later build publishes
/// a new one.</summary>
internal sealed record SymbolIndexSnapshot(
    Guid RepoId,
    IReadOnlyList<IReadOnlyList<SymbolRow>> Files,
    SymbolIndexProgress Progress)
{
    public static SymbolIndexSnapshot None { get; } = new(Guid.Empty, [], new SymbolIndexProgress.Complete());

    public IEnumerable<SymbolRow> Symbols => Files.SelectMany(f => f);
}

/// <summary>
/// The declarations in every source file of one working tree, kept in step with the files listed
/// on each <see cref="Refresh"/>: a file whose size and last write are unchanged is not parsed
/// again. Holds names and positions only — never trees or text.
/// </summary>
/// <remarks>
/// One refresh at a time: the caller serialises them. Parsing runs on a few below-normal priority
/// threads, so a first build of a large repository leaves the UI and git the machine's attention.
/// </remarks>
internal sealed class RepoSymbolIndex
{
    public const long MaxFileBytes = 1024 * 1024;
    private const int SniffBytes = 8 * 1024;
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(250);

    private readonly Guid _repoId;
    private readonly string _root;
    private readonly ISymbolExtractor _extractor;
    private readonly int _parallelism;
    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);

    public RepoSymbolIndex(Guid repoId, string root, ISymbolExtractor extractor, int? parallelism = null)
    {
        _repoId = repoId;
        _root = root;
        _extractor = extractor;
        _parallelism = parallelism ?? Math.Max(1, Environment.ProcessorCount / 2);
    }

    private readonly record struct FileKey(long Size, DateTime LastWriteUtc);

    private sealed record Entry(FileKey Key, IReadOnlyList<SymbolRow> Symbols);

    private sealed record Parsed(string Path, FileKey Key, IReadOnlyList<SymbolRow> Symbols);

    /// <summary>The index as it stands, marked complete.</summary>
    public SymbolIndexSnapshot Current => Snapshot(new SymbolIndexProgress.Complete());

    /// <summary>
    /// Brings the index in line with <paramref name="listed"/> (repo-relative paths): parses what is
    /// new or changed and drops what is gone. <paramref name="publish"/> is called as files finish,
    /// then once more, complete.
    /// </summary>
    public void Refresh(IReadOnlyList<string> listed, Action<SymbolIndexSnapshot> publish, CancellationToken cancel)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var toParse = new List<(string Path, CodeLanguage Language, FileKey Key)>();
        foreach (var path in listed)
        {
            if (CodeLanguages.Detect(path) is not { } language || !SymbolIndexLanguages.Indexes(language)) continue;
            if (KeyOf(path) is not { } key) continue;

            wanted.Add(path);
            if (_files.TryGetValue(path, out var known) && known.Key == key) continue;
            toParse.Add((path, language, key));
        }

        foreach (var gone in _files.Keys.Where(p => !wanted.Contains(p)).ToList())
            _files.Remove(gone);

        var total = wanted.Count;
        var done = total - toParse.Count;
        if (toParse.Count > 0)
        {
            publish(Snapshot(new SymbolIndexProgress.Building(done, total)));
            ParseAll(toParse, parsed =>
            {
                foreach (var file in parsed)
                    _files[file.Path] = new Entry(file.Key, file.Symbols);
                done += parsed.Count;
                publish(Snapshot(new SymbolIndexProgress.Building(done, total)));
            }, cancel);
        }

        cancel.ThrowIfCancellationRequested();
        publish(Snapshot(new SymbolIndexProgress.Complete()));
    }

    private void ParseAll(
        List<(string Path, CodeLanguage Language, FileKey Key)> work,
        Action<List<Parsed>> landed,
        CancellationToken cancel)
    {
        var results = new ConcurrentQueue<Parsed>();
        var next = -1;
        var threads = new Thread[Math.Min(_parallelism, work.Count)];
        for (var t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                int i;
                while (!cancel.IsCancellationRequested && (i = Interlocked.Increment(ref next)) < work.Count)
                {
                    var (path, language, key) = work[i];
                    results.Enqueue(new Parsed(path, key, Extract(path, language)));
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Symbol index",
            };
            threads[t].Start();
        }

        foreach (var thread in threads)
            while (!thread.Join(PublishEvery))
                Drain(results, landed);

        Drain(results, landed);
    }

    private static void Drain(ConcurrentQueue<Parsed> results, Action<List<Parsed>> landed)
    {
        var batch = new List<Parsed>();
        while (results.TryDequeue(out var parsed)) batch.Add(parsed);
        if (batch.Count > 0) landed(batch);
    }

    private FileKey? KeyOf(string path)
    {
        try
        {
            var info = new FileInfo(Absolute(path));
            return info.Exists ? new FileKey(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A file's declarations, or none for one too large, binary, unreadable or unparsable.</summary>
    private IReadOnlyList<SymbolRow> Extract(string path, CodeLanguage language)
    {
        byte[] bytes;
        try
        {
            var absolute = Absolute(path);
            if (new FileInfo(absolute).Length > MaxFileBytes) return [];
            bytes = File.ReadAllBytes(absolute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        if (bytes.Length > MaxFileBytes || IsBinary(bytes)) return [];
        var outline = _extractor.Extract(FileTextDecoder.DecodeText(bytes), language);
        if (outline is null) return [];

        var rows = new List<SymbolRow>();
        foreach (var root in outline.Roots) Collect(root, container: null, path, rows);
        return rows;
    }

    /// <summary>Every declaration under a node, each with the nearest type around it. Namespaces are
    /// walked through but not listed: every file of one declares the same one.</summary>
    private static void Collect(OutlineNode node, string? container, string path, List<SymbolRow> rows)
    {
        if (node.Kind != SymbolKind.Namespace)
            rows.Add(new SymbolRow(node.Name, node.Kind, container, node.ParameterTypes, path, node.NameLine, node.NameColumn));

        var inner = node.Kind == SymbolKind.Namespace ? container : node.Name;
        foreach (var child in node.Children) Collect(child, inner, path, rows);
    }

    private static bool IsBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, SniffBytes);
        for (var i = 0; i < limit; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private string Absolute(string path) => System.IO.Path.Combine(_root, path);

    private SymbolIndexSnapshot Snapshot(SymbolIndexProgress progress)
    {
        var files = new List<IReadOnlyList<SymbolRow>>(_files.Count);
        foreach (var entry in _files.Values)
            if (entry.Symbols.Count > 0)
                files.Add(entry.Symbols);
        return new SymbolIndexSnapshot(_repoId, files, progress);
    }
}

internal static class SymbolIndexLanguages
{
    /// <summary>
    /// Whether a language's declarations belong in symbol search. Data and markup languages have
    /// outlines too — a JSON file's keys, a document's headings — but a repository has thousands of
    /// those, and they would bury the declarations search is for.
    /// </summary>
    public static bool Indexes(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp or CodeLanguage.TypeScript or CodeLanguage.Tsx or CodeLanguage.JavaScript
            or CodeLanguage.Python or CodeLanguage.Go or CodeLanguage.Rust or CodeLanguage.Java
            or CodeLanguage.Bash or CodeLanguage.C or CodeLanguage.Svelte => true,
        CodeLanguage.Json or CodeLanguage.Css or CodeLanguage.Html or CodeLanguage.Markdown
            or CodeLanguage.Yaml or CodeLanguage.Toml or CodeLanguage.MarkdownInline => false,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Not classified."),
    };
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Theming;

using Xunit;
using Xunit.Abstractions;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// Measures the shipped TextMate highlighter against a tree-sitter one over this checkout's own
/// source, and writes the numbers to a markdown report.
/// </summary>
/// <remarks>
/// Gated on <c>GITBENCH_HIGHLIGHT_BENCH=1</c> because it walks the whole repository and takes
/// minutes: this answers a design question once, it is not a regression test. Set
/// <c>GITBENCH_HIGHLIGHT_BENCH_OUT</c> to choose where the report lands.
/// </remarks>
public sealed class HighlightBenchmarkTests(ITestOutputHelper output)
{
    private const int FilesPerLanguage = 120;
    private const int Repeats = 3;
    private const int ConcurrencyFiles = 500;

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    [Fact]
    public void MeasureAgainstTextMate()
    {
        if (Environment.GetEnvironmentVariable("GITBENCH_HIGHLIGHT_BENCH") != "1")
        {
            output.WriteLine("Set GITBENCH_HIGHLIGHT_BENCH=1 to run the highlighter benchmark.");
            return;
        }

        var report = new StringBuilder();
        var languages = LoadProbes(report, out var probes);

        try
        {
            var measurements = Measure(languages, probes, report);
            MeasureConcurrency(probes, report);
            ReportGuardrails(report);
            ReportQuality(languages, probes, report);
            ReportSnippets(probes, report);
            _ = measurements;
        }
        finally
        {
            foreach (var probe in probes.Values) probe.Dispose();
        }

        var path = Path.Combine(
            Environment.GetEnvironmentVariable("GITBENCH_HIGHLIGHT_BENCH_OUT") ?? Path.GetTempPath(),
            "highlight-benchmark.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString());
        output.WriteLine($"Report written to {path}");
    }

    private static List<CodeLanguage> LoadProbes(
        StringBuilder report,
        out Dictionary<CodeLanguage, TreeSitterHighlightProbe> probes)
    {
        report.AppendLine("# Tree-sitter vs TextMate highlighting");
        report.AppendLine();
        report.AppendLine(
            $"- Host: {Environment.OSVersion}, {Environment.ProcessorCount} logical cores, " +
            $".NET {Environment.Version}, {Configuration} build");
        report.AppendLine($"- Corpus: this checkout, {FilesPerLanguage} files per language max, " +
            $"best of {Repeats} runs per file");
        report.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine();
        report.AppendLine("## Query compilation");
        report.AppendLine();
        report.AppendLine("| Language | Patterns kept | Patterns in file | Note |");
        report.AppendLine("| --- | --- | --- | --- |");

        probes = [];
        var languages = new List<CodeLanguage>();

        foreach (var language in CodeLanguages.All)
        {
            var query = BenchCorpus.Query(language);
            if (query is null)
            {
                report.AppendLine($"| {language} | — | — | no upstream highlights.scm |");
                continue;
            }

            try
            {
                var probe = TreeSitterHighlightProbe.Create(language, query);
                probes.Add(language, probe);
                languages.Add(language);
                var note = probe.PatternsKept == probe.PatternsTotal ? "" : "patterns dropped, see below";
                report.AppendLine($"| {language} | {probe.PatternsKept} | {probe.PatternsTotal} | {note} |");
            }
            catch (Exception error)
            {
                report.AppendLine($"| {language} | — | — | failed: {Escape(error.Message)} |");
            }
        }

        report.AppendLine();
        return languages;
    }

    private List<LanguageMeasurement> Measure(
        List<CodeLanguage> languages,
        Dictionary<CodeLanguage, TreeSitterHighlightProbe> probes,
        StringBuilder report)
    {
        var highlighter = SyntaxHighlighter.Shared;
        var results = new List<LanguageMeasurement>();

        foreach (var language in languages)
        {
            var files = BenchCorpus.Files(language, FilesPerLanguage);
            if (files.Count == 0) continue;

            var probe = probes[language];
            var measurement = new LanguageMeasurement(language);

            foreach (var file in files)
            {
                var text = BenchCorpus.ReadText(file.Path);
                if (text is null) continue;

                var languageId = LanguageRegistry.DetectLanguageId(file.Path);
                if (languageId is null) continue;

                // Warm both engines on this file before timing it: the first touch of a grammar
                // loads and caches it, which is a startup cost, not a per-file one.
                _ = highlighter.Highlight(text, languageId);
                _ = probe.Highlight(text);

                var textMate = double.MaxValue;
                var textMateFellBack = false;
                var treeSitter = double.MaxValue;
                var parse = double.MaxValue;
                var query = double.MaxValue;
                var build = double.MaxValue;

                for (var repeat = 0; repeat < Repeats; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    var spans = highlighter.Highlight(text, languageId);
                    watch.Stop();
                    textMate = Math.Min(textMate, watch.Elapsed.TotalMilliseconds);
                    textMateFellBack |= spans is null;

                    var run = probe.Highlight(text);
                    treeSitter = Math.Min(treeSitter, run.Total.TotalMilliseconds);
                    parse = Math.Min(parse, run.Parse.TotalMilliseconds);
                    query = Math.Min(query, run.Query.TotalMilliseconds);
                    build = Math.Min(build, run.Build.TotalMilliseconds);
                }

                measurement.Add(file, text.Length, textMate, textMateFellBack, treeSitter, parse, query, build);
            }

            results.Add(measurement);
        }

        report.AppendLine("## Throughput");
        report.AppendLine();
        report.AppendLine("Per-file wall time in milliseconds, best of three.");
        report.AppendLine();
        report.AppendLine("| Language | Files | KB | TextMate med | TextMate p95 | TextMate max | " +
            "tree-sitter med | tree-sitter p95 | tree-sitter max | Speedup (total) | TM plain |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var measurement in results)
        {
            report.AppendLine(
                $"| {measurement.Language} | {measurement.Count} | {measurement.Kilobytes:F0} | " +
                $"{Ms(measurement.TextMate.Median)} | {Ms(measurement.TextMate.P95)} | " +
                $"{Ms(measurement.TextMate.Max)} | " +
                $"{Ms(measurement.TreeSitter.Median)} | {Ms(measurement.TreeSitter.P95)} | " +
                $"{Ms(measurement.TreeSitter.Max)} | " +
                $"{measurement.TextMate.Total / Math.Max(measurement.TreeSitter.Total, 0.0001):F1}x | " +
                $"{measurement.FellBack} |");
        }

        var allTextMate = results.Sum(r => r.TextMate.Total);
        var allTreeSitter = results.Sum(r => r.TreeSitter.Total);
        var allBytes = results.Sum(r => r.Kilobytes);
        report.AppendLine();
        report.AppendLine(
            $"**Whole corpus:** {results.Sum(r => r.Count)} files, {allBytes / 1024:F1} MB. " +
            $"TextMate {allTextMate:F0} ms ({allBytes / 1024 / (allTextMate / 1000):F1} MB/s), " +
            $"tree-sitter {allTreeSitter:F0} ms ({allBytes / 1024 / (allTreeSitter / 1000):F1} MB/s) — " +
            $"**{allTextMate / allTreeSitter:F1}x**.");
        report.AppendLine();

        report.AppendLine("## Is tree-sitter ever the slower one?");
        report.AppendLine();
        report.AppendLine("| Language | Files where tree-sitter is slower | Worst ratio (files TextMate actually tokenized) |");
        report.AppendLine("| --- | --- | --- |");

        foreach (var measurement in results)
        {
            report.AppendLine(
                $"| {measurement.Language} | {measurement.TreeSitterSlower} / {measurement.Count} | " +
                $"{measurement.WorstTreeSitter} |");
        }

        report.AppendLine();
        report.AppendLine("## Where the tree-sitter time goes");
        report.AppendLine();
        report.AppendLine("Capability A already parses every file it shows a hunk header for. " +
            "The parse column is the cost that is already being paid; query plus build is the " +
            "marginal cost of highlighting from that tree.");
        report.AppendLine();
        report.AppendLine("| Language | Parse ms | Query ms | Build ms | Marginal share | " +
            "TextMate ms vs marginal |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var measurement in results)
        {
            var marginal = measurement.QueryTotal + measurement.BuildTotal;
            report.AppendLine(
                $"| {measurement.Language} | {measurement.ParseTotal:F0} | {measurement.QueryTotal:F0} | " +
                $"{measurement.BuildTotal:F0} | {marginal / measurement.TreeSitter.Total * 100:F0}% | " +
                $"{measurement.TextMate.Total / Math.Max(marginal, 0.0001):F1}x |");
        }

        report.AppendLine();
        return results;
    }

    private static void MeasureConcurrency(
        Dictionary<CodeLanguage, TreeSitterHighlightProbe> probes,
        StringBuilder report)
    {
        report.AppendLine("## Concurrency");
        report.AppendLine();

        if (!probes.ContainsKey(CodeLanguage.CSharp))
        {
            report.AppendLine("Skipped: no C# probe.");
            report.AppendLine();
            return;
        }

        var files = BenchCorpus.Files(CodeLanguage.CSharp, ConcurrencyFiles)
            .Select(f => (File: f, Text: BenchCorpus.ReadText(f.Path)))
            .Where(f => f.Text is not null)
            .Select(f => f.Text!)
            .ToArray();

        var highlighter = SyntaxHighlighter.Shared;
        var workers = Environment.ProcessorCount;

        foreach (var text in files) _ = highlighter.Highlight(text, "csharp");

        var sequential = Stopwatch.StartNew();
        foreach (var text in files) _ = highlighter.Highlight(text, "csharp");
        sequential.Stop();

        var parallelTextMate = Stopwatch.StartNew();
        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            text => highlighter.Highlight(text, "csharp"));
        parallelTextMate.Stop();

        // Built before the clock starts, the way a pool would be: compiling the query is a
        // once-per-process cost and charging it to the parallel run would measure the setup.
        var query = BenchCorpus.Query(CodeLanguage.CSharp)!;
        var perWorker = Enumerable.Range(0, workers)
            .Select(_ => TreeSitterHighlightProbe.Create(CodeLanguage.CSharp, query))
            .ToArray();

        var sequentialTreeSitter = Stopwatch.StartNew();
        foreach (var text in files) _ = probes[CodeLanguage.CSharp].Highlight(text);
        sequentialTreeSitter.Stop();

        var parallelTreeSitter = Stopwatch.StartNew();
        Parallel.For(0, workers, worker =>
        {
            for (var i = worker; i < files.Length; i += workers) perWorker[worker].Highlight(files[i]);
        });
        parallelTreeSitter.Stop();

        foreach (var probe in perWorker) probe.Dispose();

        report.AppendLine($"{files.Length} C# files, {workers} workers. TextMate serializes every " +
            "surface through one lock; tree-sitter parsers are per-worker.");
        report.AppendLine();
        report.AppendLine("| Engine | 1 thread | " + workers + " threads | Scaling |");
        report.AppendLine("| --- | --- | --- | --- |");
        report.AppendLine(
            $"| TextMate | {sequential.Elapsed.TotalMilliseconds:F0} ms | " +
            $"{parallelTextMate.Elapsed.TotalMilliseconds:F0} ms | " +
            $"{sequential.Elapsed.TotalMilliseconds / parallelTextMate.Elapsed.TotalMilliseconds:F2}x |");
        report.AppendLine(
            $"| tree-sitter | {sequentialTreeSitter.Elapsed.TotalMilliseconds:F0} ms | " +
            $"{parallelTreeSitter.Elapsed.TotalMilliseconds:F0} ms | " +
            $"{sequentialTreeSitter.Elapsed.TotalMilliseconds / parallelTreeSitter.Elapsed.TotalMilliseconds:F2}x |");
        report.AppendLine();
    }

    private static void ReportGuardrails(StringBuilder report)
    {
        report.AppendLine("## Files nobody highlights today");
        report.AppendLine();
        report.AppendLine($"`SyntaxHighlighter.MaxFileChars` is {SyntaxHighlighter.MaxFileChars / 1024} KB; " +
            $"`TreeSitterSymbolExtractor.MaxFileBytes` is {TreeSitterSymbolExtractor.MaxFileBytes / 1024} KB. " +
            "Files between the two render plain today and would not have to.");
        report.AppendLine();

        var oversized = BenchCorpus.Oversized();
        var betweenCaps = oversized
            .Where(f => f.Bytes <= TreeSitterSymbolExtractor.MaxFileBytes)
            .ToList();

        report.AppendLine($"- Over {SyntaxHighlighter.MaxFileChars / 1024} KB in this checkout: " +
            $"{oversized.Count(f => f.Bytes > SyntaxHighlighter.MaxFileChars)} files in a bundled language.");
        report.AppendLine($"- Between the two caps (plain today, highlightable by tree-sitter): " +
            $"{betweenCaps.Count} files.");
        report.AppendLine();

        foreach (var file in oversized.Take(10))
        {
            report.AppendLine($"  - `{file.RelativePath}` — {file.Bytes / 1024} KB");
        }

        report.AppendLine();
    }

    private static void ReportQuality(
        List<CodeLanguage> languages,
        Dictionary<CodeLanguage, TreeSitterHighlightProbe> probes,
        StringBuilder report)
    {
        report.AppendLine("## Agreement and coverage");
        report.AppendLine();
        report.AppendLine("Measured per non-whitespace character of every corpus file, after both " +
            "engines are reduced to the same `TokenColorSlot` vocabulary.");
        report.AppendLine();
        report.AppendLine("| Language | TextMate colored | tree-sitter colored | Same slot | " +
            "Only TextMate | Only tree-sitter | Both, different |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        var highlighter = SyntaxHighlighter.Shared;

        foreach (var language in languages)
        {
            var files = BenchCorpus.Files(language, FilesPerLanguage);
            long total = 0, textMateColored = 0, treeSitterColored = 0, same = 0, onlyTextMate = 0,
                onlyTreeSitter = 0, differ = 0;

            foreach (var file in files)
            {
                var text = BenchCorpus.ReadText(file.Path);
                if (text is null) continue;
                var languageId = LanguageRegistry.DetectLanguageId(file.Path);
                if (languageId is null) continue;

                var textMate = highlighter.Highlight(text, languageId);
                if (textMate is null) continue;
                var treeSitter = probes[language].Highlight(text).Spans;

                var lines = text.ReplaceLineEndings("\n").Split('\n');
                var count = Math.Min(Math.Min(lines.Length, textMate.Count), treeSitter.Count);

                for (var i = 0; i < count; i++)
                {
                    var expanded = DiffText.ExpandTabs(lines[i]);
                    var left = Paint(expanded.Length, textMate[i]);
                    var right = Paint(expanded.Length, treeSitter[i]);

                    for (var c = 0; c < expanded.Length; c++)
                    {
                        if (char.IsWhiteSpace(expanded[c])) continue;
                        total++;
                        var l = left[c];
                        var r = right[c];
                        if (l != TokenColorSlot.Default) textMateColored++;
                        if (r != TokenColorSlot.Default) treeSitterColored++;

                        if (l == r && l != TokenColorSlot.Default) same++;
                        else if (l != TokenColorSlot.Default && r == TokenColorSlot.Default) onlyTextMate++;
                        else if (l == TokenColorSlot.Default && r != TokenColorSlot.Default) onlyTreeSitter++;
                        else if (l != r) differ++;
                    }
                }
            }

            if (total == 0) continue;

            report.AppendLine(
                $"| {language} | {Percent(textMateColored, total)} | {Percent(treeSitterColored, total)} | " +
                $"{Percent(same, total)} | {Percent(onlyTextMate, total)} | " +
                $"{Percent(onlyTreeSitter, total)} | {Percent(differ, total)} |");
        }

        report.AppendLine();
    }

    private static void ReportSnippets(
        Dictionary<CodeLanguage, TreeSitterHighlightProbe> probes,
        StringBuilder report)
    {
        report.AppendLine("## Side by side");
        report.AppendLine();
        report.AppendLine("One letter per column: `K`eyword `S`tring `C`omment `N`umber `T`ype " +
            "`F`unction `V`ariable `O`perator `P`unctuation con`X`tant, `.` for uncolored.");
        report.AppendLine();

        var highlighter = SyntaxHighlighter.Shared;

        foreach (var (language, languageId, source) in HighlightSnippets.All)
        {
            if (!probes.TryGetValue(language, out var probe)) continue;

            report.AppendLine($"### {language}");
            report.AppendLine();
            report.AppendLine("```text");

            var textMate = highlighter.Highlight(source, languageId);
            var treeSitter = probe.Highlight(source).Spans;
            var lines = source.ReplaceLineEndings("\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var expanded = DiffText.ExpandTabs(lines[i]);
                if (expanded.Trim().Length == 0) continue;

                report.AppendLine($"     {expanded}");
                report.AppendLine($"  tm {Letters(expanded, textMate is null || i >= textMate.Count ? [] : textMate[i])}");
                report.AppendLine($"  ts {Letters(expanded, i >= treeSitter.Count ? [] : treeSitter[i])}");
            }

            report.AppendLine("```");
            report.AppendLine();
        }
    }

    private static TokenColorSlot[] Paint(int length, IReadOnlyList<TokenSpan> spans)
    {
        var slots = new TokenColorSlot[length];
        foreach (var span in spans)
        {
            var end = Math.Min(span.Start + span.Length, length);
            for (var i = Math.Max(span.Start, 0); i < end; i++) slots[i] = span.Slot;
        }

        return slots;
    }

    private static string Letters(string line, IReadOnlyList<TokenSpan> spans)
    {
        var slots = Paint(line.Length, spans);
        var letters = new char[line.Length];
        for (var i = 0; i < line.Length; i++)
        {
            letters[i] = char.IsWhiteSpace(line[i]) ? ' ' : Letter(slots[i]);
        }

        return new string(letters);
    }

    private static char Letter(TokenColorSlot slot) => slot switch
    {
        TokenColorSlot.Keyword => 'K',
        TokenColorSlot.String => 'S',
        TokenColorSlot.Comment => 'C',
        TokenColorSlot.Number => 'N',
        TokenColorSlot.Type => 'T',
        TokenColorSlot.Function => 'F',
        TokenColorSlot.Variable => 'V',
        TokenColorSlot.Operator => 'O',
        TokenColorSlot.Punctuation => 'P',
        TokenColorSlot.Constant => 'X',
        TokenColorSlot.Default => '.',
        _ => 'm',
    };

    private static string Percent(long part, long total) =>
        (100.0 * part / total).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Ms(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Escape(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|");

    private sealed class LanguageMeasurement(CodeLanguage language)
    {
        private readonly List<double> _textMate = [];
        private readonly List<double> _treeSitter = [];

        public CodeLanguage Language { get; } = language;

        public int Count { get; private set; }

        public int FellBack { get; private set; }

        public double Kilobytes { get; private set; }

        public double ParseTotal { get; private set; }

        public double QueryTotal { get; private set; }

        public double BuildTotal { get; private set; }

        public int TreeSitterSlower { get; private set; }

        public string WorstTreeSitter { get; private set; } = "";

        private double _worstRatio;

        public Stats TextMate => Stats.Of(_textMate);

        public Stats TreeSitter => Stats.Of(_treeSitter);

        public void Add(
            CorpusFile file,
            int chars,
            double textMate,
            bool fellBack,
            double treeSitter,
            double parse,
            double query,
            double build)
        {
            Count++;
            Kilobytes += chars / 1024.0;

            // A file TextMate declined is not a race it lost: it returned null without tokenizing,
            // so a ratio against it would score a fallback as a tree-sitter defeat.
            if (!fellBack)
            {
                var ratio = treeSitter / Math.Max(textMate, 0.0001);
                if (ratio > 1) TreeSitterSlower++;
                if (ratio > _worstRatio)
                {
                    _worstRatio = ratio;
                    WorstTreeSitter =
                        $"`{file.RelativePath}` {treeSitter:F2} ms vs {textMate:F2} ms ({ratio:F2}x)";
                }
            }

            if (fellBack) FellBack++;
            _textMate.Add(textMate);
            _treeSitter.Add(treeSitter);
            ParseTotal += parse;
            QueryTotal += query;
            BuildTotal += build;
        }
    }

    private readonly record struct Stats(double Median, double P95, double Max, double Total)
    {
        public static Stats Of(List<double> values)
        {
            if (values.Count == 0) return default;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            return new Stats(
                sorted[sorted.Length / 2],
                sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))],
                sorted[^1],
                values.Sum());
        }
    }
}

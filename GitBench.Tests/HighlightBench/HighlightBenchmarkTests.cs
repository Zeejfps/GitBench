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
/// Measures the two engines behind <see cref="RoutedSyntaxHighlighter"/> against each other over
/// this checkout's own source, and writes the numbers to a markdown report.
/// </summary>
/// <remarks>
/// Gated on <c>GITBENCH_HIGHLIGHT_BENCH=1</c> because it walks the whole repository and takes
/// minutes: this settled a design question once and re-answers it after a grammar bump, it is not
/// a regression test. Set <c>GITBENCH_HIGHLIGHT_BENCH_OUT</c> to choose where the report lands.
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

    // The corpus is gathered per grammar, but both engines are addressed by TextMate's language id.
    private static readonly (CodeLanguage Language, string LanguageId)[] Routed =
    [
        (CodeLanguage.CSharp, "csharp"),
        (CodeLanguage.TypeScript, "typescript"),
        (CodeLanguage.Tsx, "typescriptreact"),
        (CodeLanguage.JavaScript, "javascript"),
        (CodeLanguage.Json, "json"),
        (CodeLanguage.Css, "css"),
        (CodeLanguage.Yaml, "yaml"),
        (CodeLanguage.Python, "python"),
        (CodeLanguage.Go, "go"),
        (CodeLanguage.Rust, "rust"),
        (CodeLanguage.Java, "java"),
        (CodeLanguage.Bash, "shellscript"),
        (CodeLanguage.C, "c"),
        (CodeLanguage.Markdown, "markdown"),
        (CodeLanguage.Html, "html"),
    ];

    [Fact]
    public void MeasureAgainstTextMate()
    {
        if (Environment.GetEnvironmentVariable("GITBENCH_HIGHLIGHT_BENCH") != "1")
        {
            output.WriteLine("Set GITBENCH_HIGHLIGHT_BENCH=1 to run the highlighter benchmark.");
            return;
        }

        using var treeSitter = new TreeSitterSyntaxHighlighter();
        var textMate = SyntaxHighlighter.Shared;
        var report = new StringBuilder();

        Preamble(treeSitter, report);
        Measure(treeSitter, textMate, report);
        MeasureConcurrency(treeSitter, textMate, report);
        ReportGuardrails(report);
        ReportQuality(treeSitter, textMate, report);
        ReportSnippets(treeSitter, textMate, report);

        var path = Path.Combine(
            Environment.GetEnvironmentVariable("GITBENCH_HIGHLIGHT_BENCH_OUT") ?? Path.GetTempPath(),
            "highlight-benchmark.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString());
        output.WriteLine($"Report written to {path}");
    }

    private static void Preamble(TreeSitterSyntaxHighlighter treeSitter, StringBuilder report)
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
        report.AppendLine("Both engines as the app ships them: `TreeSitterSyntaxHighlighter` with " +
            "its embedded queries, and `SyntaxHighlighter` behind it. Markdown and HTML are in the " +
            "corpus and are the interesting rows: both are parsed several times over, once per " +
            "injected region.");
        report.AppendLine();

        var unavailable = Routed.Where(r => !treeSitter.Supports(r.LanguageId)).ToArray();
        if (unavailable.Length > 0)
        {
            report.AppendLine($"**{unavailable.Length} language(s) failed to load a query and are " +
                $"falling back to TextMate: {string.Join(", ", unavailable.Select(u => u.Language))}.**");
            report.AppendLine();
        }
    }

    private static void Measure(
        TreeSitterSyntaxHighlighter treeSitter,
        ISyntaxHighlighter textMate,
        StringBuilder report)
    {
        var results = new List<LanguageMeasurement>();

        foreach (var (language, languageId) in Routed)
        {
            var files = BenchCorpus.Files(language, FilesPerLanguage);
            if (files.Count == 0) continue;

            var measurement = new LanguageMeasurement(language);

            foreach (var file in files)
            {
                var text = BenchCorpus.ReadText(file.Path);
                if (text is null) continue;

                // Warm both on this file before timing it: the first touch of a grammar loads and
                // caches it, which is a startup cost, not a per-file one.
                _ = textMate.Highlight(text, languageId);
                _ = treeSitter.Highlight(text, languageId);

                var textMateBest = double.MaxValue;
                var treeSitterBest = double.MaxValue;
                var fellBack = false;

                for (var repeat = 0; repeat < Repeats; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    var spans = textMate.Highlight(text, languageId);
                    watch.Stop();
                    textMateBest = Math.Min(textMateBest, watch.Elapsed.TotalMilliseconds);
                    fellBack |= spans is null;

                    watch.Restart();
                    _ = treeSitter.Highlight(text, languageId);
                    watch.Stop();
                    treeSitterBest = Math.Min(treeSitterBest, watch.Elapsed.TotalMilliseconds);
                }

                measurement.Add(file, text.Length, textMateBest, fellBack, treeSitterBest);
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

        foreach (var m in results)
        {
            report.AppendLine(
                $"| {m.Language} | {m.Count} | {m.Kilobytes:F0} | " +
                $"{Ms(m.TextMate.Median)} | {Ms(m.TextMate.P95)} | {Ms(m.TextMate.Max)} | " +
                $"{Ms(m.TreeSitter.Median)} | {Ms(m.TreeSitter.P95)} | {Ms(m.TreeSitter.Max)} | " +
                $"{m.TextMate.Total / Math.Max(m.TreeSitter.Total, 0.0001):F1}x | {m.FellBack} |");
        }

        var allTextMate = results.Sum(r => r.TextMate.Total);
        var allTreeSitter = results.Sum(r => r.TreeSitter.Total);
        var allKb = results.Sum(r => r.Kilobytes);
        report.AppendLine();
        report.AppendLine(
            $"**Whole corpus:** {results.Sum(r => r.Count)} files, {allKb / 1024:F1} MB. " +
            $"TextMate {allTextMate:F0} ms ({allKb / 1024 / (allTextMate / 1000):F1} MB/s), " +
            $"tree-sitter {allTreeSitter:F0} ms ({allKb / 1024 / (allTreeSitter / 1000):F1} MB/s) — " +
            $"**{allTextMate / allTreeSitter:F1}x**.");
        report.AppendLine();

        report.AppendLine("## Is tree-sitter ever the slower one?");
        report.AppendLine();
        report.AppendLine("| Language | Files where tree-sitter is slower | " +
            "Worst ratio (files TextMate actually tokenized) |");
        report.AppendLine("| --- | --- | --- |");

        foreach (var m in results)
        {
            report.AppendLine($"| {m.Language} | {m.TreeSitterSlower} / {m.Count} | {m.WorstTreeSitter} |");
        }

        report.AppendLine();
    }

    private static void MeasureConcurrency(
        TreeSitterSyntaxHighlighter treeSitter,
        ISyntaxHighlighter textMate,
        StringBuilder report)
    {
        report.AppendLine("## Concurrency");
        report.AppendLine();

        var files = BenchCorpus.Files(CodeLanguage.CSharp, ConcurrencyFiles)
            .Select(f => BenchCorpus.ReadText(f.Path))
            .OfType<string>()
            .ToArray();
        var workers = Environment.ProcessorCount;

        foreach (var text in files)
        {
            _ = textMate.Highlight(text, "csharp");
            _ = treeSitter.Highlight(text, "csharp");
        }

        var sequentialTextMate = Time(() =>
        {
            foreach (var text in files) _ = textMate.Highlight(text, "csharp");
        });

        var parallelTextMate = Time(() => Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            text => textMate.Highlight(text, "csharp")));

        var sequentialTreeSitter = Time(() =>
        {
            foreach (var text in files) _ = treeSitter.Highlight(text, "csharp");
        });

        var parallelTreeSitter = Time(() => Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            text => treeSitter.Highlight(text, "csharp")));

        report.AppendLine($"{files.Length} C# files, {workers} workers — the shape of the review " +
            "window, which starts a lane per visible file. TextMate serializes every surface " +
            "through one lock; tree-sitter takes a parser per worker from its pool.");
        report.AppendLine();
        report.AppendLine($"| Engine | 1 thread | {workers} threads | Scaling |");
        report.AppendLine("| --- | --- | --- | --- |");
        report.AppendLine($"| TextMate | {sequentialTextMate:F0} ms | {parallelTextMate:F0} ms | " +
            $"{sequentialTextMate / parallelTextMate:F2}x |");
        report.AppendLine($"| tree-sitter | {sequentialTreeSitter:F0} ms | {parallelTreeSitter:F0} ms | " +
            $"{sequentialTreeSitter / parallelTreeSitter:F2}x |");
        report.AppendLine();
    }

    private static double Time(Action work)
    {
        var watch = Stopwatch.StartNew();
        work();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static void ReportGuardrails(StringBuilder report)
    {
        report.AppendLine("## Files nobody highlights today");
        report.AppendLine();
        report.AppendLine($"`SyntaxHighlighter.MaxFileChars` is {SyntaxHighlighter.MaxFileChars / 1024} KB; " +
            $"`TreeSitterSyntaxHighlighter.MaxFileBytes` is {TreeSitterSyntaxHighlighter.MaxFileBytes / 1024} KB. " +
            "Files between the two rendered plain before routing and no longer have to.");
        report.AppendLine();

        var oversized = BenchCorpus.Oversized();
        report.AppendLine($"- Over {SyntaxHighlighter.MaxFileChars / 1024} KB in this checkout: " +
            $"{oversized.Count(f => f.Bytes > SyntaxHighlighter.MaxFileChars)} files in a bundled language.");
        report.AppendLine($"- Between the two caps (was plain, now colored): " +
            $"{oversized.Count(f => f.Bytes <= TreeSitterSyntaxHighlighter.MaxFileBytes)} files.");
        report.AppendLine();

        foreach (var file in oversized.Take(10))
        {
            report.AppendLine($"  - `{file.RelativePath}` — {file.Bytes / 1024} KB");
        }

        report.AppendLine();
    }

    private static void ReportQuality(
        TreeSitterSyntaxHighlighter treeSitter,
        ISyntaxHighlighter textMate,
        StringBuilder report)
    {
        report.AppendLine("## Agreement and coverage");
        report.AppendLine();
        report.AppendLine("Per non-whitespace character of every corpus file, both engines reduced " +
            "to the same `TokenColorSlot` vocabulary.");
        report.AppendLine();
        report.AppendLine("| Language | TextMate colored | tree-sitter colored | Same slot | " +
            "Only TextMate | Only tree-sitter | Both, different |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var (language, languageId) in Routed)
        {
            long total = 0, left = 0, right = 0, same = 0, onlyLeft = 0, onlyRight = 0, differ = 0;

            foreach (var file in BenchCorpus.Files(language, FilesPerLanguage))
            {
                var text = BenchCorpus.ReadText(file.Path);
                if (text is null) continue;

                var textMateSpans = textMate.Highlight(text, languageId);
                var treeSitterSpans = treeSitter.Highlight(text, languageId);
                if (textMateSpans is null || treeSitterSpans is null) continue;

                var lines = text.ReplaceLineEndings("\n").Split('\n');
                var count = Math.Min(Math.Min(lines.Length, textMateSpans.Count), treeSitterSpans.Count);

                for (var i = 0; i < count; i++)
                {
                    var expanded = DiffText.ExpandTabs(lines[i]);
                    var a = Paint(expanded.Length, textMateSpans[i]);
                    var b = Paint(expanded.Length, treeSitterSpans[i]);

                    for (var c = 0; c < expanded.Length; c++)
                    {
                        if (char.IsWhiteSpace(expanded[c])) continue;
                        total++;
                        if (a[c] != TokenColorSlot.Default) left++;
                        if (b[c] != TokenColorSlot.Default) right++;

                        if (a[c] == b[c] && a[c] != TokenColorSlot.Default) same++;
                        else if (a[c] != TokenColorSlot.Default && b[c] == TokenColorSlot.Default) onlyLeft++;
                        else if (a[c] == TokenColorSlot.Default && b[c] != TokenColorSlot.Default) onlyRight++;
                        else if (a[c] != b[c]) differ++;
                    }
                }
            }

            if (total == 0) continue;

            report.AppendLine(
                $"| {language} | {Percent(left, total)} | {Percent(right, total)} | " +
                $"{Percent(same, total)} | {Percent(onlyLeft, total)} | " +
                $"{Percent(onlyRight, total)} | {Percent(differ, total)} |");
        }

        report.AppendLine();
    }

    private static void ReportSnippets(
        TreeSitterSyntaxHighlighter treeSitter,
        ISyntaxHighlighter textMate,
        StringBuilder report)
    {
        report.AppendLine("## Side by side");
        report.AppendLine();
        report.AppendLine("One letter per column: `K`eyword `S`tring `C`omment `N`umber `T`ype " +
            "`F`unction `V`ariable `O`perator `P`unctuation con`X`tant, `.` for uncolored.");
        report.AppendLine();

        foreach (var (language, languageId, source) in HighlightSnippets.All)
        {
            report.AppendLine($"### {language}");
            report.AppendLine();
            report.AppendLine("```text");

            var left = textMate.Highlight(source, languageId);
            var right = treeSitter.Highlight(source, languageId);
            var lines = source.ReplaceLineEndings("\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var expanded = DiffText.ExpandTabs(lines[i]);
                if (expanded.Trim().Length == 0) continue;

                report.AppendLine($"     {expanded}");
                report.AppendLine($"  tm {Letters(expanded, SpansAt(left, i))}");
                report.AppendLine($"  ts {Letters(expanded, SpansAt(right, i))}");
            }

            report.AppendLine("```");
            report.AppendLine();
        }
    }

    private static IReadOnlyList<TokenSpan> SpansAt(IReadOnlyList<IReadOnlyList<TokenSpan>>? spans, int line) =>
        spans is null || line >= spans.Count ? [] : spans[line];

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

    private sealed class LanguageMeasurement(CodeLanguage language)
    {
        private readonly List<double> _textMate = [];
        private readonly List<double> _treeSitter = [];
        private double _worstRatio;

        public CodeLanguage Language { get; } = language;

        public int Count { get; private set; }

        public int FellBack { get; private set; }

        public double Kilobytes { get; private set; }

        public int TreeSitterSlower { get; private set; }

        public string WorstTreeSitter { get; private set; } = "";

        public Stats TextMate => Stats.Of(_textMate);

        public Stats TreeSitter => Stats.Of(_treeSitter);

        public void Add(CorpusFile file, int chars, double textMate, bool fellBack, double treeSitter)
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

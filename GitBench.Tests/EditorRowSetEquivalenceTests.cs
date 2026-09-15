using System.Text;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>Pins <see cref="EditorRowSet"/> against <see cref="DiffRowSet"/> over the repository's own files.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class EditorRowSetEquivalenceTests(CodeIntelFixture fixture)
{
    private const int MinimumCorpusFiles = 120;
    private const int MaxFileBytes = 512 * 1024;

    [Fact]
    public void TheCorpusIsBigEnoughToBeWorthRunning()
    {
        var corpus = Corpus();

        Assert.True(corpus.Count >= MinimumCorpusFiles, $"Only {corpus.Count} corpus files were found.");
        foreach (var extension in new[] { ".cs", ".md", ".json" })
            Assert.Contains(corpus, file => Path.GetExtension(file).Equals(extension, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCorpusFileProjectsToTheViewersRowsPlusTheEmptyLineAFinalNewlineOpens()
    {
        var loc = Loc();
        var compared = 0;

        foreach (var path in Corpus())
        {
            var text = ReadText(path);
            if (text is null) continue;

            var lines = TextLines.Split(text);
            var viewer = DiffRowSet.Build(FullFileOf(path, lines), loc);
            var editor = new EditorRowSet(TextDocument.FromText(text), loc);

            AssertSameStream(path, text, lines, viewer, editor);
            compared += viewer.Rows.Count;
        }

        Assert.True(compared > 100_000, $"Only {compared} rows were compared.");
    }

    [Fact]
    public void SyntaxSpansReachTheRowsTheSameWayOnBothSides()
    {
        var loc = Loc();

        foreach (var path in Corpus().Take(40))
        {
            var text = ReadText(path);
            if (text is null) continue;

            var lines = TextLines.Split(text);
            var highlight = HighlightOf(lines);
            var state = FullFileOf(path, lines) with
            {
                Annotations = new DiffAnnotations(highlight, null, null),
            };

            var viewer = DiffRowSet.Build(state, loc);
            var editor = new EditorRowSet(TextDocument.FromText(text), loc, highlight);

            AssertSameStream(path, text, lines, viewer, editor);
            if (lines.Any(line => line.Length > 0))
                Assert.Contains(editor.Rows, row => row is DiffRow.Line { Spans.Count: > 0 });
        }
    }

    [Fact]
    public void FoldsAndUsagesRowsLandOnTheSameRowsOnBothSides()
    {
        var loc = Loc();
        var folded = 0;

        foreach (var path in Corpus().Where(p => p.EndsWith(".cs", StringComparison.Ordinal)).Take(30))
        {
            var text = ReadText(path);
            if (text is null) continue;

            var lines = TextLines.Split(text);
            if (fixture.Extractor.Extract(string.Join('\n', lines), CodeLanguage.CSharp) is not { } outline)
                continue;
            var state = FullFileOf(path, lines) with { Annotations = new DiffAnnotations(null, outline, null) };

            foreach (var folds in FoldStates(path, outline))
            {
                var viewer = DiffRowSet.Build(state, loc, folds, usageLens: true);
                var document = TextDocument.FromText(text);
                var editor = new EditorRowSet(document, loc) { UsageLensRows = true };
                editor.SetFolds(folds);
                Assert.True(editor.SetAnnotations(new Revised<DiffAnnotations>(
                    DocumentRevision.Of(document), new DiffAnnotations(null, outline, null))));

                AssertSameStream(path, text, lines, viewer, editor, folds);
                folded++;
            }
        }

        Assert.True(folded > 60, $"Only {folded} fold states were compared.");
    }

    private static IEnumerable<FoldState?> FoldStates(string path, FileOutline outline)
    {
        yield return FoldState.Open(path);
        foreach (var id in Paths(outline.Roots, null).Take(4))
            yield return FoldState.Open(path).Toggled(id);
    }

    private static IEnumerable<string> Paths(IReadOnlyList<OutlineNode> nodes, string? parent)
    {
        foreach (var node in nodes)
        {
            var path = FileOutline.PathOf(parent, node);
            if (node.SignatureEndLine < node.EndLine) yield return path;
            foreach (var child in Paths(node.Children, path)) yield return child;
        }
    }

    private static void AssertSameStream(
        string path,
        string text,
        IReadOnlyList<string> lines,
        DiffRowSet viewer,
        EditorRowSet editor,
        FoldState? folds = null)
    {
        var trailing = editor.Rows.Count - viewer.Rows.Count;
        Assert.True(
            trailing is 0 or 1,
            $"{path}: {editor.Rows.Count} editor rows against {viewer.Rows.Count} viewer rows.");
        Assert.Equal(OpensATrailingLine(text), trailing == 1);

        for (var i = 0; i < viewer.Rows.Count; i++)
            AssertSameRow($"{path} row {i}", viewer.Rows[i], editor.Rows[i]);

        if (trailing == 1)
        {
            var last = Assert.IsType<DiffRow.Line>(editor.Rows[^1]);
            Assert.Equal(string.Empty, last.Text.Raw);
            Assert.Equal(DiffLineKind.Context, last.Kind);
            Assert.Equal(new FileLine(lines.Count + 1), last.NewNumber.Line!.Value);
        }

        var viewerRows = (IDiffRowSource)viewer;
        Assert.True(viewer.SingleGutter && editor.SingleGutter, $"{path}: not a single-gutter render.");
        Assert.Equal(folds != null, viewer.FoldColumn);
        Assert.Equal(viewer.FoldColumn, editor.FoldColumn);
        Assert.Equal(viewerRows.HiddenText is null, editor.HiddenText is null);
        Assert.False(viewer.GlyphColumn || editor.GlyphColumn, $"{path}: a glyph column was reserved.");

        Assert.Equal(viewer.MaxRowCells, editor.MaxRowCells);
        Assert.Equal(DigitCount(lines.Count + trailing), editor.GutterDigits);

        for (var line = 1; line <= lines.Count; line++)
        {
            var expected = viewer.RowForNewLine(new FileLine(line));
            Assert.Equal(expected, editor.RowForNewLine(new FileLine(line)));
            Assert.Equal(
                viewer.RowNearestNewLine(new FileLine(line)),
                editor.RowNearestNewLine(new FileLine(line)));
        }

        for (var row = 0; row < viewer.Rows.Count; row++)
        {
            var at = new RowIndex(row);
            Assert.Equal(viewer.NewLineAt(at), editor.NewLineAt(at));
            Assert.Equal(viewer.AnchorAt(at), editor.AnchorAt(at));
            if (viewerRows.HiddenText is { } hidden)
                Assert.Equal(hidden(at), editor.HiddenText!(at));
        }
    }

    private static void AssertSameRow(string where, DiffRow expected, DiffRow actual)
    {
        if (expected is DiffRow.Lens lens)
        {
            Assert.Equal(lens, Assert.IsType<DiffRow.Lens>(actual));
            return;
        }

        var a = Assert.IsType<DiffRow.Line>(expected);
        var b = Assert.IsType<DiffRow.Line>(actual);

        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.OldNumber, b.OldNumber);
        Assert.Equal(a.NewNumber, b.NewNumber);
        Assert.Equal(a.Text.Raw, b.Text.Raw);
        Assert.Equal(a.Text.Expanded, b.Text.Expanded);
        Assert.Equal(a.Fold, b.Fold);
        Assert.Equal(a.Emphasis, b.Emphasis);

        if (a.Spans is null || b.Spans is null)
        {
            Assert.True(a.Spans is null && b.Spans is null, $"{where}: only one side carries spans.");
            return;
        }

        Assert.Equal(a.Spans.Count, b.Spans.Count);
        for (var i = 0; i < a.Spans.Count; i++) Assert.Equal(a.Spans[i], b.Spans[i]);
    }

    private static bool OpensATrailingLine(string text) =>
        text.Length == 0 || text[^1] is '\n' or '\r';

    private static DiffRenderState.FullFile FullFileOf(string path, IReadOnlyList<string> lines) =>
        new(path, lines, new HashSet<int>(), DiffSide.Unstaged, Truncated: false);

    private static DiffHighlight HighlightOf(IReadOnlyList<string> lines)
    {
        var spans = new List<IReadOnlyList<TokenSpan>>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var expanded = DiffText.ExpandTabs(lines[i]);
            spans.Add(expanded.Length == 0
                ? Array.Empty<TokenSpan>()
                : new[] { new TokenSpan(0, Math.Min(4, expanded.Length), TokenColorSlot.Keyword) });
        }
        return new DiffHighlight(null, spans);
    }

    private static IReadOnlyList<string> Corpus() => CorpusFiles.Value;

    private static readonly Lazy<IReadOnlyList<string>> CorpusFiles = new(() =>
    {
        var extensions = new[] { ".cs", ".md", ".json" };
        var excluded = new[] { ".git", "bin", "obj", "node_modules", "artifacts", ".artifacts", ".vs" };
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(HighlightBench.BenchCorpus.RepoRoot);

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
                if (!excluded.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    stack.Push(child);

            foreach (var file in Directory.GetFiles(directory))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal)) continue;
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    });

    private static string? ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        return new UTF8Encoding(false, false).GetString(bytes);
    }

    private static int DigitCount(int n)
    {
        if (n <= 0) return 1;
        var d = 0;
        while (n > 0) { d++; n /= 10; }
        return d;
    }

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));
}

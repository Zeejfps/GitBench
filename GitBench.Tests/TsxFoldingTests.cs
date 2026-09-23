using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Folding what a React component is made of rather than only what it declares: the hook callbacks,
/// the JSX tree, the branches. A component file declares one thing, so folding only declarations
/// left it with one chevron.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class TsxFoldingTests(CodeIntelFixture fixture)
{
    private const string Source = """
        import {
          useEffect,
          useState,
        } from "react";

        export function Panel({ title }: { title: string }) {
          const [open, setOpen] = useState(false);
          useEffect(() => {
            console.log(title);
            setOpen(true);
          }, [title]);
          if (open) {
            setOpen(false);
            return null;
          } else {
            setOpen(true);
            return null;
          }
          return (
            <div className="panel">
              <section>
                <h1>{title}</h1>
                <p>body</p>
              </section>
            </div>
          );
        }
        """;

    [Fact]
    public void TheComponentsInsidesCarryChevronsOfTheirOwn()
    {
        var chevrons = ChevronLines(Rows(Open()));

        Assert.Contains(Line("import {"), chevrons);
        Assert.Contains(Line("export function Panel({ title }: { title: string }) {"), chevrons);
        Assert.Contains(Line("  useEffect(() => {"), chevrons);
        Assert.Contains(Line("  if (open) {"), chevrons);
        Assert.Contains(Line("  } else {"), chevrons);
        Assert.Contains(Line("    <div className=\"panel\">"), chevrons);
        Assert.Contains(Line("      <section>"), chevrons);
    }

    // The component's body is the declaration's fold; its statement block is not a second one.
    [Fact]
    public void EveryLineCarriesAtMostOneChevron()
    {
        var outline = fixture.Outline(Source, CodeLanguage.Tsx);

        var panel = Line("export function Panel({ title }: { title: string }) {");
        Assert.DoesNotContain(outline.Regions, r => r.StartLine == panel);
        Assert.Equal(outline.Regions.Count, outline.Regions.Select(r => r.StartLine).Distinct().Count());
    }

    // A folded element reads as one line: its opening tag, the chip, its closing tag.
    [Fact]
    public void CollapsingAnElementPullsItsClosingTagUpBehindTheChip()
    {
        var rows = Rows(Collapsed(IdAt(Line("    <div className=\"panel\">"))));

        Assert.Contains(rows, r => Text(r) == "    <div className=\"panel\">");
        Assert.DoesNotContain(rows, r => Text(r) == "    </div>");
        Assert.DoesNotContain(rows, r => Text(r) == "      <section>");
        Assert.DoesNotContain(rows, r => Text(r) == "        <h1>{title}</h1>");

        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        Assert.Equal("    <div className=\"panel\">", chip.Text.Raw);
        var joined = Assert.IsType<FoldChip.Joined>(chip.Fold!.Value.Chip);
        Assert.Equal("</div>", joined.Text);
        Assert.Equal(Line("    </div>"), joined.Closing.Value);
    }

    // The brace opening the body is already on the signature's line, so the chip must not stand
    // for it again: `export function Panel(...) {{...}` drew two.
    [Fact]
    public void ADeclarationOpeningOnItsSignaturesLineDoesNotDoubleTheBrace()
    {
        var signature = "export function Panel({ title }: { title: string }) {";
        var id = Rows(Open()).OfType<DiffRow.Line>().Single(r => r.Text.Raw == signature).Fold!.Value.Id;

        var rows = Rows(Collapsed(id));

        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        Assert.Equal(signature, chip.Text.Raw);
        var joined = Assert.IsType<FoldChip.Joined>(chip.Fold!.Value.Chip);
        Assert.Equal("}", joined.Text);
        Assert.Equal(FullFileRow.RegionChipText, FullFileRow.ChipText(joined));
    }

    [Fact]
    public void APulledUpClosingLineKeepsItsColours()
    {
        var rows = Rows(Collapsed(IdAt(Line("  useEffect(() => {"))), highlighted: true);

        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        var joined = Assert.IsType<FoldChip.Joined>(chip.Fold!.Value.Chip);
        Assert.Equal("}, [title]);", joined.Text);
        Assert.NotNull(joined.Spans);
        Assert.All(joined.Spans!, s => Assert.InRange(s.Start + s.Length, 1, joined.Text.Length));
    }

    // The closing line is hidden now too, so a copy across the fold has to put it back.
    [Fact]
    public void CopyingAcrossAJoinedFoldBringsTheClosingLineBack()
    {
        var set = Set(Collapsed(IdAt(Line("    <div className=\"panel\">"))), highlighted: false);
        var span = DiffSelectionModel.WholeSpan(set.Rows);
        Assert.NotNull(span);

        var copied = DiffSelectionModel.BuildCopyText(set.Rows, span.Value.Start, span.Value.End, set.HiddenAfter)
            .ReplaceLineEndings("\n")
            .Split('\n');

        Assert.Contains("      </section>", copied);
        Assert.Single(copied, l => l == "    </div>");
    }

    // The closing line staying is what keeps the else branch reachable when the if branch folds.
    [Fact]
    public void CollapsingTheIfBranchLeavesTheElseOnScreen()
    {
        var rows = Rows(Collapsed(IdAt(Line("  if (open) {"))));

        Assert.DoesNotContain(rows, r => Text(r) == "    setOpen(false);");
        Assert.Contains(rows, r => Text(r) == "  } else {");
        Assert.Contains(rows, r => r is DiffRow.Line { Fold.Chip: FoldChip.Interior } l && l.Text.Raw == "  if (open) {");
        Assert.Contains(rows, r => Text(r) == "    setOpen(true);");
        Assert.Contains(rows, r => r is DiffRow.Line { Fold.Chevron: true } l && l.Text.Raw == "  } else {");
    }

    [Fact]
    public void AnInnerRegionUnderACollapsedOuterOneHasNoChevron()
    {
        var rows = Rows(Collapsed(IdAt(Line("    <div className=\"panel\">"))));

        Assert.DoesNotContain(rows, r => r is DiffRow.Line { Fold.Chevron: true } l && l.Text.Raw == "      <section>");
    }

    // Keyed by what the line says rather than where it is, so typing above a fold leaves it shut.
    [Fact]
    public void ARegionKeepsItsIdAcrossAnEditAboveIt()
    {
        var before = fixture.Outline(Source, CodeLanguage.Tsx).Regions
            .Single(r => r.StartLine == Line("      <section>")).Id;

        var edited = Source.Replace("  const [open, setOpen]", "  const extra = 1;\n  const [open, setOpen]");
        var after = fixture.Outline(edited, CodeLanguage.Tsx).Regions
            .Single(r => r.StartLine == Line("      <section>") + 1).Id;

        Assert.Equal(before, after);
    }

    [Fact]
    public void TwoRegionsOpeningOnTheSameTextAreToldApart()
    {
        const string twice = """
            const a = [
              1,
              2,
            ];
            function f() {
              if (x) {
                a();
                b();
              }
              if (x) {
                a();
                b();
              }
            }
            """;

        var regions = fixture.Outline(twice, CodeLanguage.Tsx).Regions;

        var ifs = regions.Where(r => r.StartLine is 6 or 10).ToArray();
        Assert.Equal(2, ifs.Length);
        Assert.NotEqual(ifs[0].Id, ifs[1].Id);
    }

    [Fact]
    public void AFileDeclaringNothingStillFolds()
    {
        const string bare = """
            render(
              <App>
                <Route />
              </App>,
            );
            """;

        var outline = fixture.Outline(bare, CodeLanguage.Tsx);

        Assert.Empty(outline.Roots);
        Assert.NotEmpty(outline.Regions);
    }

    [Fact]
    public void TheBundledTsxFoldQueryCompiles()
    {
        var log = new List<string>();
        using var grammars = new TreeSitterGrammars(log.Add);

        Assert.NotNull(grammars.Get(CodeLanguage.Tsx)?.Folds);
        Assert.DoesNotContain(log, l => l.Contains("Folding", StringComparison.Ordinal));
    }

    private string IdAt(int line) =>
        fixture.Outline(Source, CodeLanguage.Tsx).Regions.Single(r => r.StartLine == line).Id;

    private static int Line(string text) =>
        Array.IndexOf(Source.ReplaceLineEndings("\n").Split('\n'), text) is var i and >= 0
            ? i + 1
            : throw new ArgumentException($"No line reads '{text}'.", nameof(text));

    private static FoldState Open() => FoldState.Open(Path);

    private static FoldState Collapsed(string id) => FoldState.Open(Path).Toggled(id);

    private const string Path = "src/Panel.tsx";

    private IReadOnlyList<DiffRow> Rows(FoldState folds, bool highlighted = false) => Set(folds, highlighted).Rows;

    private DiffRowSet Set(FoldState folds, bool highlighted)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var highlight = highlighted && fixture.Highlighter.Highlight(Source, CodeLanguage.Tsx) is { } spans
            ? new DiffHighlight(null, spans)
            : null;
        var state = new DiffRenderState.FullFile(
            Path,
            lines,
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(highlight, fixture.Outline(Source, CodeLanguage.Tsx), null));
        return DiffRowSet.Build(state, new LocalizationService(new State<Locale>(Locale.En)), folds);
    }

    private static IReadOnlyList<int> ChevronLines(IReadOnlyList<DiffRow> rows) =>
        rows.OfType<DiffRow.Line>()
            .Where(r => r.Fold is { Chevron: true })
            .Select(r => r.NewNumber.Line!.Value.Value)
            .ToArray();

    private static string Text(DiffRow row) => row is DiffRow.Line line ? line.Text.Raw : string.Empty;
}

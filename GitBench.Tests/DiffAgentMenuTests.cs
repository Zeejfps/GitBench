using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>What a diff selection asks the agent: three ready questions, each about the code it
/// quotes.</summary>
public sealed class DiffAgentMenuTests
{
    private static readonly IReadOnlyList<DiffRow> Rows =
    [
        new DiffRow.Line(
            DiffLineKind.Added, DiffGutterNumber.None, DiffGutterNumber.Of(new FileLine(42)),
            DiffLineText.Of("    Modern();")),
    ];

    [Fact]
    public void EachQuestion_GoesToTheAgentWithTheSelectionQuoted()
    {
        var quote = new CodeQuote.InDiff(DiffSelectionQuote.Build(
            Rows, new DiffTextPos(default, default), new DiffTextPos(default, new ExpandedColumn(13)),
            "src/Runner.cs")!);
        var asks = new List<(CodeQuote Quote, string Question)>();
        using var loc = new LocalizationService(new State<Locale>(Locale.En));

        var items = DiffAgentMenu.Items(loc.Strings.Value, quote, (q, question) => asks.Add((q, question)));
        foreach (var item in items) item.OnSelected();

        Assert.Equal(["Explain this", "What could break?", "Suggest a fix"], items.Select(i => i.Label));
        Assert.Equal(["Explain this selection.", "What could break here?", "Suggest a fix for this."], asks.Select(a => a.Question));
        Assert.All(asks, a => Assert.Same(quote, a.Quote));
    }
}

using GitBench.Controls;
using GitBench.Features.Editor;
using GitBench.Features.Repos;
using GitBench.Localization;

namespace GitBench.Features.Diff;

/// <summary>
/// What a live diff selection asks the repository's agent: three ready questions about the code it
/// quotes.
/// </summary>
/// <remarks>
/// The questions are written here in English rather than pulled from the string catalogs: they are
/// addressed to the agent, not to the reader. Only the menu labels are localized.
/// </remarks>
internal static class DiffAgentMenu
{
    private const string ExplainAsk = "Explain this selection.";
    private const string BreakageAsk = "What could break here?";
    private const string FixAsk = "Suggest a fix for this.";

    public static IReadOnlyList<RepoBarContextMenu.Item> Items(
        Strings strings,
        CodeQuote quote,
        Action<CodeQuote, string> ask) =>
    [
        new RepoBarContextMenu.Item(strings.AssistantAskExplain, () => ask(quote, ExplainAsk), LucideIcons.FileText),
        new RepoBarContextMenu.Item(strings.AssistantAskBreakage, () => ask(quote, BreakageAsk), LucideIcons.TriangleAlert),
        new RepoBarContextMenu.Item(strings.AssistantAskFix, () => ask(quote, FixAsk), LucideIcons.PencilLine),
    ];
}

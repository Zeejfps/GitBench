using GitBench.Controls;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Messages;

namespace GitBench.Features.Diff;

/// <summary>
/// What a live diff selection offers the assistant: three one-shot questions and a free-form one.
/// </summary>
/// <remarks>
/// The question each preset carries is written here in English rather than pulled from the string
/// catalogs, for the same reason the agent prompts are: it is addressed to the model, not to the
/// reader. Only the menu labels are localized. What language the answer comes back in is settled by
/// the live context block, as it is for every other turn.
/// </remarks>
internal static class DiffAssistantMenu
{
    private const string ExplainAsk = "Explain this selection.";
    private const string BreakageAsk = "What could break here?";
    private const string FixAsk = "Suggest a fix for this.";

    public static IReadOnlyList<RepoBarContextMenu.Item> Items(
        Strings strings,
        IMessageBus bus,
        DiffSelectionQuote quote)
    {
        void Ask(string? agent, string? question) =>
            bus.Broadcast(new AskAssistantAboutSelectionMessage(agent, quote.ToPrompt(question)));

        return
        [
            new RepoBarContextMenu.Item(
                strings.AssistantAskExplain,
                () => Ask(AgentCatalog.ExplainSelectionAgent, ExplainAsk),
                LucideIcons.FileText),
            new RepoBarContextMenu.Item(
                strings.AssistantAskBreakage,
                () => Ask(AgentCatalog.BreakageSelectionAgent, BreakageAsk),
                LucideIcons.TriangleAlert),
            new RepoBarContextMenu.Item(
                strings.AssistantAskFix,
                () => Ask(AgentCatalog.FixSelectionAgent, FixAsk),
                LucideIcons.PencilLine),
            RepoBarContextMenu.Separator,
            new RepoBarContextMenu.Item(
                strings.AssistantAskFreeform,
                () => Ask(null, null),
                LucideIcons.SquareTerminal),
        ];
    }
}

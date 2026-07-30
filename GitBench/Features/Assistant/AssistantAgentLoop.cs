using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Git;
using GitBench.Localization;

namespace GitBench.Features.Assistant;

/// <summary>
/// Drives one exchange: send the conversation, run whatever tools the model asked for, send the
/// results back, repeat until it stops asking.
/// </summary>
/// <remarks>
/// The conversation list passed in is grown in place, so a caller that keeps it across turns keeps
/// the model's own view of the exchange.
/// </remarks>
internal sealed class AssistantAgentLoop
{
    // A ceiling on tool rounds, so a model that keeps calling tools can't spin forever.
    public const int MaxToolRounds = 24;

    private readonly IAssistantBackend _backend;
    private readonly AgentDefinition _agent;
    private readonly AssistantToolset _toolset;
    private readonly Func<bool> _toolCallingIsUnproven;

    private bool _calledATool;
    private bool _reportedNoToolSupport;

    /// <param name="toolCallingIsUnproven">Whether this provider's tool calling is a question rather
    /// than a guarantee. When it is, a conversation that never calls a tool says so once instead of
    /// letting a model that cannot call them answer from nothing.</param>
    public AssistantAgentLoop(
        IAssistantBackend backend,
        AgentDefinition agent,
        AssistantToolset toolset,
        Func<bool>? toolCallingIsUnproven = null)
    {
        _backend = backend;
        _agent = agent;
        _toolset = toolset;
        _toolCallingIsUnproven = toolCallingIsUnproven ?? (static () => false);
    }

    /// <summary>The live repo state a turn is given as its <c>repoContext</c>: which checkout the
    /// question is about, and the language to answer in.</summary>
    /// <remarks>The language is taken from the active catalog's own culture, so the language list
    /// lives in one place; the synthetic Pseudo locale is built on the reference catalog's culture and
    /// so reads as English rather than asking the model for gibberish.</remarks>
    public static string RepoStateBlock(IGitService git, Repo repo, ILocalizationService loc)
    {
        var branch = RepoHead.Branch(git, repo) ?? "(detached or unknown)";
        return $"Repository: {repo.DisplayName}\nPath: {repo.Path}\nChecked-out branch: {branch}\n"
               + $"Reply in {loc.Strings.Value.Culture.EnglishName}.";
    }

    public async IAsyncEnumerable<AssistantEvent> RunAsync(
        List<AssistantMessage> conversation,
        string? repoContext,
        IToolApprovalGate approvals,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Live repo state rides in the message list, never in the top-level system block, so the
        // cached tools + system prefix survives between turns.
        if (!string.IsNullOrWhiteSpace(repoContext))
            conversation.Add(new AssistantMessage.RepoContext(repoContext));

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var turn = new AssistantTurn(_agent.Tier, _agent.SystemPrompt, conversation.ToArray());
            var text = new StringBuilder();
            var toolUses = new List<AssistantContent.ToolUse>();
            var stop = StopReason.EndTurn;
            AssistantEvent? terminal = null;

            await foreach (var backendEvent in _backend.SendAsync(turn, _toolset.Tools, ct).ConfigureAwait(false))
            {
                switch (backendEvent)
                {
                    case BackendEvent.TextDelta delta:
                        text.Append(delta.Text);
                        yield return new AssistantEvent.TextDelta(delta.Text);
                        break;
                    case BackendEvent.Thinking:
                        yield return new AssistantEvent.Thinking();
                        break;
                    case BackendEvent.ToolUse use:
                        toolUses.Add(new AssistantContent.ToolUse(use.Id, use.Name, use.Input));
                        _calledATool = true;
                        break;
                    case BackendEvent.TurnComplete complete:
                        stop = complete.Reason;
                        break;
                    // A refusal is settled here, before anything reads what the turn accumulated —
                    // it arrives as a stop reason on an otherwise successful response whose content
                    // may be empty or partial.
                    case BackendEvent.Refusal refusal:
                        terminal = new AssistantEvent.Refused(refusal.Category, refusal.Explanation);
                        break;
                    case BackendEvent.Error error:
                        terminal = new AssistantEvent.Failed(error.Message, error.Detail);
                        break;
                }
            }

            if (terminal is not null)
            {
                yield return terminal;
                yield break;
            }

            var content = new List<AssistantContent>(toolUses.Count + 1);
            if (text.Length > 0)
                content.Add(new AssistantContent.Text(text.ToString()));
            content.AddRange(toolUses);
            if (content.Count > 0)
                conversation.Add(new AssistantMessage.Assistant(content));

            if (toolUses.Count == 0)
            {
                if (ShouldReportNoToolSupport())
                {
                    _reportedNoToolSupport = true;
                    yield return new AssistantEvent.NoToolSupport();
                }

                yield return new AssistantEvent.Completed(stop);
                yield break;
            }

            // One assistant message can carry several tool_use blocks; all of their results go back
            // in a single user message, or the model stops calling tools in parallel.
            var results = new List<AssistantToolResult>(toolUses.Count);
            foreach (var use in toolUses)
            {
                ct.ThrowIfCancellationRequested();

                // Reads run silently. Anything that changes the repository stops here until the
                // person says so, and a refusal is an error result rather than a dead turn — the
                // model can explain itself or take a different route.
                var tool = _toolset.Find(use.Name);
                if (tool is { IsWrite: true }
                    && !await approvals.RequestAsync(use.Name, use.Input, ct).ConfigureAwait(false))
                {
                    results.Add(new AssistantToolResult(use.Id, Declined(use.Name), true));
                    continue;
                }

                yield return new AssistantEvent.ToolStarted(use.Id, use.Name, use.Input);
                var invocation = await InvokeAsync(tool, use, ct).ConfigureAwait(false);
                results.Add(new AssistantToolResult(use.Id, invocation.Content, invocation.IsError));
                yield return new AssistantEvent.ToolFinished(use.Id, use.Name, invocation.IsError);
            }

            conversation.Add(new AssistantMessage.ToolResults(results));
        }

        yield return new AssistantEvent.Failed(
            $"The assistant kept calling tools past {MaxToolRounds} rounds and was stopped.",
            null);
    }

    // Said once per conversation, and only where tool calling was never demonstrated: a model that
    // answered from nothing looks exactly like one that had nothing to say.
    private bool ShouldReportNoToolSupport() =>
        !_calledATool
        && !_reportedNoToolSupport
        && _toolset.Tools.Count > 0
        && _toolCallingIsUnproven();

    // What the model is told when the person says no. It is an ordinary tool error, so the turn
    // carries on and the model gets to respond to the refusal.
    private static string Declined(string name) =>
        $"The person declined to run '{name}'. Do not call it again unless they ask; say what you "
        + "intended to do and offer an alternative if there is one.";

    private static async Task<ToolInvocation> InvokeAsync(
        IAssistantTool? tool,
        AssistantContent.ToolUse use,
        CancellationToken ct)
    {
        if (tool is null)
            return ToolInvocation.Error($"No tool named '{use.Name}' is available.");

        try
        {
            return await tool.InvokeAsync(use.Input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolInvocation.Error(ex.Message);
        }
    }
}

/// What the loop reports as a turn runs. Exactly one of Completed, Refused or Failed ends it.
internal abstract record AssistantEvent
{
    private AssistantEvent() { }

    public sealed record TextDelta(string Text) : AssistantEvent;

    public sealed record Thinking : AssistantEvent;

    public sealed record ToolStarted(string Id, string Name, JsonElement Input) : AssistantEvent;

    public sealed record ToolFinished(string Id, string Name, bool IsError) : AssistantEvent;

    /// <summary>The model answered without ever calling a tool on a provider whose tool calling is
    /// not a given. Carries no text: what the reader is told is the view's to word.</summary>
    public sealed record NoToolSupport : AssistantEvent;

    public sealed record Completed(StopReason Reason) : AssistantEvent;

    public sealed record Refused(string? Category, string? Explanation) : AssistantEvent;

    public sealed record Failed(string Message, string? Detail) : AssistantEvent;
}

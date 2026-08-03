using System.Text;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant;

/// <summary>
/// How one headless agent run ended: whether the artifact tool actually landed, whether tool
/// calling was possible at all, whatever the model said in prose, and whether it failed or was
/// stopped.
/// </summary>
internal readonly record struct OneShotOutcome(
    bool ArtifactLanded,
    bool ToolsWereNeverPossible,
    string Reply,
    string? Failure,
    bool Cancelled)
{
    public bool Succeeded => ArtifactLanded && Failure is null && !Cancelled;
}

/// <summary>
/// Runs one agent to completion with nobody watching, for a caller that wants a result rather
/// than a conversation.
/// </summary>
/// <remarks>
/// The result arrives as a named tool call, not as the turn's reply. Prose is a poor carrier for
/// anything structured — the first thing a model writes is as likely to be a preamble, a fence or
/// an apology as the answer — so the run reports whether that one call succeeded and leaves the
/// tool to write through whatever surface it was built against.
///
/// Everything about which model, which tools and which rubric comes from the
/// <see cref="AssistantAgentLoop"/> it is handed, so a new one-shot feature is an agent file and a
/// tool rather than a second pipeline.
/// </remarks>
internal sealed class OneShotAgentRun
{
    private readonly AssistantAgentLoop _loop;
    private readonly IToolApprovalGate _approvals;
    private readonly string _artifactTool;

    /// <param name="artifactTool">The tool whose successful call means the run produced what it
    /// was asked for.</param>
    public OneShotAgentRun(AssistantAgentLoop loop, IToolApprovalGate approvals, string artifactTool)
    {
        _loop = loop;
        _approvals = approvals;
        _artifactTool = artifactTool;
    }

    /// <summary>Short status lines as the run proceeds, for a caller with somewhere to show them.
    /// Called off the UI thread.</summary>
    public Action<string>? OnProgress { get; set; }

    public async Task<OneShotOutcome> RunAsync(string instruction, string? repoContext, CancellationToken ct)
    {
        // A fresh conversation every time: this is a one-shot, and nothing about the last run
        // should steer the next one.
        var conversation = new List<AssistantMessage> { new AssistantMessage.User(instruction) };
        var reply = new StringBuilder();
        var landed = false;
        var toolsWereNeverPossible = false;
        string? failure = null;

        try
        {
            await foreach (var e in _loop.RunAsync(conversation, repoContext, _approvals, ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case AssistantEvent.TextDelta delta:
                        reply.Append(delta.Text);
                        break;
                    case AssistantEvent.ToolStarted started:
                        OnProgress?.Invoke(started.Name);
                        break;
                    case AssistantEvent.ToolFinished finished when finished.Name == _artifactTool:
                        landed = !finished.IsError;
                        break;
                    case AssistantEvent.NoToolSupport:
                        toolsWereNeverPossible = true;
                        break;
                    case AssistantEvent.Refused refused:
                        failure = refused.Explanation;
                        break;
                    case AssistantEvent.Failed failed:
                        failure = failed.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new OneShotOutcome(false, false, reply.ToString(), null, true);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        return new OneShotOutcome(landed, toolsWereNeverPossible, reply.ToString(), failure, false);
    }
}

using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant;

/// <summary>
/// A detached exchange that keeps its memory: its own agent, and a message list that grows across
/// turns the way the repository's thread does — unlike a preset's, which goes with its turn. For an
/// agent that has to remember what it already did, such as a walkthrough narrator that must not
/// send the same stops twice. What each turn produces is reported to the owner as it lands in the
/// transcript, on the UI thread.
/// </summary>
internal sealed class AssistantThread
{
    public AssistantThread(AssistantAgentLoop loop, Action<AssistantEvent> observer)
    {
        Loop = loop;
        Observer = observer;
    }

    public AssistantAgentLoop Loop { get; }

    public List<AssistantMessage> Messages { get; } = [];

    public Action<AssistantEvent> Observer { get; }
}

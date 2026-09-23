using GitBench.Features.Assistant;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// What the user and the agent said to each other in the panel, across every pairing session of the
/// conversation and between them. UI thread only.
/// </summary>
internal sealed class AgentTranscript
{
    private readonly ObservableList<PairingMessage> _messages = new();
    private readonly State<State<string>?> _openNarration = new(null);

    public ObservableList<PairingMessage> Messages => _messages;

    /// <summary>The message the agent's current turn is streaming onto, if any.</summary>
    public IReadable<State<string>?> OpenNarration => _openNarration;

    public int Count => _messages.Count;

    /// <summary>Streams the agent's prose onto the message its current turn is writing.</summary>
    public void AppendNarration(string text)
    {
        if (_openNarration.Value is { } open)
        {
            open.Value += text;
            return;
        }

        var lead = text.TrimStart();
        if (lead.Length == 0) return;
        var opened = new State<string>(lead);
        _openNarration.Value = opened;
        _messages.Add(new PairingMessage.Narration(opened));
    }

    /// <summary>The agent's turn ended; its next prose is a message of its own.</summary>
    public void CloseNarration() => _openNarration.Value = null;

    /// <summary>A whole reply from the agent: a message of its own, whatever its turn streamed before it.</summary>
    public void AddReply(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        Add(new PairingMessage.Narration(new State<string>(markdown.Trim())));
    }

    public void AddFromUser(string text) => Add(new PairingMessage.FromUser(text));

    public void AddNotice(string text, NoticeTone tone) => Add(new PairingMessage.Notice(text, tone));

    /// <summary>Puts a tool call the write guard has no rule for in front of the user.</summary>
    public PendingToolApproval AskPermission(string title, string details)
    {
        var pending = new PendingToolApproval(title, details);
        Add(new PairingMessage.Approval(pending));
        return pending;
    }

    public void Add(PairingMessage message)
    {
        CloseNarration();
        _messages.Add(message);
    }

    /// <summary>Drops everything from <paramref name="start"/> on, except a question still waiting
    /// on the user: the agent is blocked on the answer.</summary>
    public void ClearFrom(int start)
    {
        CloseNarration();
        for (var i = _messages.Count - 1; i >= start; i--)
            if (_messages[i] is not PairingMessage.Approval { Pending.IsPending.Value: true })
                _messages.RemoveAt(i);
    }
}

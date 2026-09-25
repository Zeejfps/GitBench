using GitBench.Features.Assistant;
using GitBench.Features.Editor;
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
    private bool _narrationBroken;
    private bool _repliedThisTurn;

    public ObservableList<PairingMessage> Messages => _messages;

    /// <summary>The message the agent's current turn is streaming onto, if any.</summary>
    public IReadable<State<string>?> OpenNarration => _openNarration;

    public int Count => _messages.Count;

    /// <summary>The agent's turn begins: prose it writes is shown again.</summary>
    public void BeginAgentTurn() => _repliedThisTurn = false;

    /// <summary>Streams the agent's prose onto the message its current turn is writing. Prose after a
    /// reply in the same turn is not shown: it recaps the reply the user just read.</summary>
    public void AppendNarration(string text)
    {
        if (_repliedThisTurn) return;
        if (_openNarration.Value is { } open)
        {
            if (_narrationBroken && text.Trim().Length > 0)
            {
                _narrationBroken = false;
                open.Value = open.Value.TrimEnd() + "\n\n" + text.TrimStart();
                return;
            }

            open.Value += text;
            return;
        }

        var lead = text.TrimStart();
        if (lead.Length == 0) return;
        var opened = new State<string>(lead);
        _openNarration.Value = opened;
        _messages.Add(new PairingMessage.Narration(opened));
    }

    /// <summary>The agent did something between two runs of prose, a tool call: the next run starts a
    /// paragraph of its own, since the agent's pieces don't end in a line break and would otherwise
    /// run together, closing a code fence onto the next sentence.</summary>
    public void BreakNarration() => _narrationBroken = _openNarration.Value is not null;

    /// <summary>The agent's turn ended; its next prose is a message of its own.</summary>
    public void CloseNarration()
    {
        _narrationBroken = false;
        _openNarration.Value = null;
    }

    /// <summary>A whole reply from the agent: a message of its own, whatever its turn streamed before it.</summary>
    public void AddReply(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        Add(new PairingMessage.Narration(new State<string>(markdown.Trim())));
        _repliedThisTurn = true;
    }

    public void AddFromUser(string text, CodeQuote? quote = null) => Add(new PairingMessage.FromUser(text, quote));

    public void AddNotice(string text, NoticeTone tone) => Add(new PairingMessage.Notice(text, tone));

    /// <summary>Puts a tool call the write guard has no rule for in front of the user.</summary>
    /// <remarks><paramref name="allowFiles"/>, where given, is what answering "this file" grants on
    /// top of approving.</remarks>
    public PendingToolApproval AskPermission(
        string title, string details, IReadOnlyList<EditPreviewLine>? preview = null, Action? allowFiles = null)
    {
        var pending = new PendingToolApproval(title, details);
        Add(new PairingMessage.Approval(pending, preview ?? [], allowFiles is null ? null : new FileAllowance(pending, allowFiles)));
        return pending;
    }

    public void Add(PairingMessage message)
    {
        CloseNarration();
        _messages.Add(message);
    }
}

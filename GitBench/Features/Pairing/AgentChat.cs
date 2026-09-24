using GitBench.App;
using GitBench.Controls;
using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Repos;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>How pressing the agent chat came out.</summary>
internal abstract record AgentChatPress
{
    /// <summary>The repository already had a conversation, which was shown or hidden.</summary>
    public sealed record Existing : AgentChatPress;

    /// <summary>A conversation was opened with the agent picked before.</summary>
    public sealed record Opened(AgentConversation Conversation) : AgentChatPress;

    /// <summary>No agent has been picked yet: the caller asks which.</summary>
    public sealed record NeedsAgent : AgentChatPress;

    /// <summary>No repository is on screen.</summary>
    public sealed record NoRepository : AgentChatPress;
}

/// <summary>
/// The way into the repository's conversation with an agent: shows and hides it, and opens one with
/// the agent last picked when there is none. UI thread only.
/// </summary>
internal sealed class AgentChat : IDisposable
{
    private readonly PairingSessions _sessions;
    private readonly IRepoRegistry _repos;
    private readonly PreferencesService _preferences;
    private readonly AgentEndpoints _endpoints;
    private readonly ILocalizationService _loc;
    private readonly Derived<bool> _available;

    public AgentChat(
        PairingSessions sessions, IRepoRegistry repos, PreferencesService preferences, AgentEndpoints endpoints, ILocalizationService loc)
    {
        _sessions = sessions;
        _repos = repos;
        _preferences = preferences;
        _endpoints = endpoints;
        _loc = loc;
        _available = new Derived<bool>(() => repos.Active.Value is not null);
    }

    /// <summary>Whether a repository is on screen to talk about.</summary>
    public IReadable<bool> IsAvailable => _available;

    /// <summary>The agent picked last, if it is still one the app runs.</summary>
    public AcpHarness? Remembered =>
        _preferences.Current.ChatAgent is { } id ? AcpHarness.Find(new AcpHarnessId(id)) : null;

    /// <summary>Shows or hides the repository's conversation, or opens one with the agent picked
    /// last.</summary>
    public AgentChatPress Press() => Go(hideShown: true);

    /// <summary>Shows the repository's conversation, or opens one with the agent picked last.</summary>
    public AgentChatPress Reveal() => Go(hideShown: false);

    private AgentChatPress Go(bool hideShown)
    {
        if (_repos.Active.Value is not { } repo) return new AgentChatPress.NoRepository();
        if (_sessions.ConversationOf(repo.Id) is not null)
        {
            if (hideShown && _sessions.IsPanelShown(repo.Id)) _sessions.HidePanel(repo.Id);
            else _sessions.ShowPanel(repo.Id);
            return new AgentChatPress.Existing();
        }

        return Remembered is { } harness && Open(harness) is { } opened
            ? new AgentChatPress.Opened(opened)
            : new AgentChatPress.NeedsAgent();
    }

    /// <summary>Shows the repository's conversation with <paramref name="harness"/>, opening one
    /// in place of a conversation with another agent, and remembers the pick.</summary>
    public AgentConversation? Open(AcpHarness harness)
    {
        if (_repos.Active.Value is not { } repo) return null;
        _preferences.Update(p => p with { ChatAgent = harness.Id.Value });
        if (_sessions.ConversationOf(repo.Id) is { } existing && (Runs(existing, harness) || existing.IsPairing))
        {
            _sessions.ShowPanel(repo.Id);
            return existing;
        }

        var announce = _endpoints.WillEnable;
        var conversation = _sessions.OpenChat(repo, harness);
        if (announce) conversation.Transcript.AddNotice(_loc.Strings.Value.AgentChatConnectionsOn, NoticeTone.Info);
        return conversation;
    }

    /// <summary>The agents to pick from, the one talking now or picked last marked. Another agent
    /// can't take over while a pairing session runs.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> AgentMenu()
    {
        var current = _repos.Active.Value is { } repo ? _sessions.ConversationOf(repo.Id) : null;
        var marked = current is { IsGone: false, Harness: PairingHarness.Acp acp } ? acp.Harness.Id : Remembered?.Id;
        var locked = current?.IsPairing == true;
        var items = new List<RepoBarContextMenu.Item>(AcpHarness.BuiltIn.Count);
        foreach (var harness in AcpHarness.BuiltIn)
            items.Add(new RepoBarContextMenu.Item(
                harness.Label,
                () => Open(harness),
                LucideIcons.Sparkles,
                Enabled: !locked || harness.Id == marked,
                Checked: harness.Id == marked));
        return items;
    }

    private static bool Runs(AgentConversation conversation, AcpHarness harness) =>
        !conversation.IsGone && conversation.Harness is PairingHarness.Acp acp && acp.Harness.Id == harness.Id;

    public void Dispose() => _available.Dispose();
}

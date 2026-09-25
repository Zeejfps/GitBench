using GitBench.App;
using GitBench.Controls;
using GitBench.Features.AgentConnections;
using GitBench.Features.Editor;
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

/// <summary>How asking the agent something came out.</summary>
internal abstract record AgentChatAsk
{
    public sealed record Asked(AgentConversation Conversation) : AgentChatAsk;

    /// <summary>No agent is talking and none has been picked: the caller asks which.</summary>
    public sealed record NeedsAgent : AgentChatAsk;

    /// <summary>No repository is on screen.</summary>
    public sealed record NoRepository : AgentChatAsk;
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

    /// <summary>The presets to pick an agent from.</summary>
    public IReadOnlyList<AgentPreset> Presets => _preferences.Current.AgentPresets;

    /// <summary>The preset picked last, if it is still there.</summary>
    public AgentPreset? Remembered =>
        _preferences.Current.ChatAgent is { } id ? Presets.FirstOrDefault(p => p.Id.Value == id) : null;

    /// <summary>The preset to start with when there is no way to ask: the one picked last, else the
    /// first on offer.</summary>
    public AgentPreset Default => Remembered ?? (Presets.Count > 0 ? Presets[0] : AgentPreset.ClaudeCode);

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

    /// <summary>Shows the repository's conversation with <paramref name="preset"/>, opening one
    /// in place of a conversation with another agent, and remembers the pick.</summary>
    public AgentConversation? Open(AgentPreset preset)
    {
        if (_repos.Active.Value is not { } repo) return null;
        _preferences.Update(p => p with { ChatAgent = preset.Id.Value });
        if (_sessions.ConversationOf(repo.Id) is { } existing && (Runs(existing, preset) || existing.IsPairing))
        {
            _sessions.ShowPanel(repo.Id);
            return existing;
        }

        var announce = _endpoints.WillEnable;
        var conversation = _sessions.OpenChat(repo, preset);
        if (announce) conversation.Transcript.AddNotice(_loc.Strings.Value.AgentChatConnectionsOn, NoticeTone.Info);
        return conversation;
    }

    /// <summary>Says something to the repository's agent, with code the user sent along: the one
    /// talking now, else the one picked last.</summary>
    public AgentChatAsk Ask(string text, CodeQuote? quote)
    {
        if (_repos.Active.Value is not { } repo) return new AgentChatAsk.NoRepository();
        PairingHarness? harness = _sessions.LiveConversation(repo.Id)?.Harness
                                  ?? (Remembered is { } remembered ? new PairingHarness.Acp(remembered) : null);
        if (harness is null) return new AgentChatAsk.NeedsAgent();
        var announce = _sessions.LiveConversation(repo.Id) is null && _endpoints.WillEnable;
        var conversation = _sessions.Ask(repo, text, quote, harness);
        if (announce) conversation.Transcript.AddNotice(_loc.Strings.Value.AgentChatConnectionsOn, NoticeTone.Info);
        return new AgentChatAsk.Asked(conversation);
    }

    /// <summary>The agents to pick from, the one talking now or picked last marked. Another agent
    /// can't take over while a pairing session runs. <paramref name="then"/> follows a pick, with the
    /// conversation it shows.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> AgentMenu(Action<AgentConversation>? then = null)
    {
        var current = _repos.Active.Value is { } repo ? _sessions.ConversationOf(repo.Id) : null;
        var marked = current is { IsGone: false, Harness: PairingHarness.Acp acp } ? acp.Preset.Id : Remembered?.Id;
        var locked = current?.IsPairing == true;
        var presets = Presets;
        var items = new List<RepoBarContextMenu.Item>(presets.Count);
        foreach (var preset in presets)
            items.Add(new RepoBarContextMenu.Item(
                preset.Name,
                () =>
                {
                    if (Open(preset) is { } conversation) then?.Invoke(conversation);
                },
                LucideIcons.Sparkles,
                Enabled: !locked || preset.Id == marked,
                Checked: preset.Id == marked));
        return items;
    }

    private static bool Runs(AgentConversation conversation, AgentPreset preset) =>
        !conversation.IsGone && conversation.Harness is PairingHarness.Acp acp && acp.Preset.Id == preset.Id;

    public void Dispose() => _available.Dispose();
}

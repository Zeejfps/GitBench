using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>An agent chat over real sessions, for tests that need one to hand around.</summary>
internal static class AgentChatFixtures
{
    /// <summary>Conversations come from <paramref name="create"/>; by default none can be opened.</summary>
    public static AgentChat Create(
        IRepoRegistry registry,
        PreferencesService preferences,
        ILocalizationService localization,
        Func<Repo, PairingHarness, AgentOpening, AgentConversation>? create = null,
        bool connectionsOn = true) =>
        new(
            new PairingSessions(registry, create ?? ((_, _, _) => throw new InvalidOperationException("No agent in this test."))),
            registry,
            preferences,
            new AgentEndpoints(
                new State<AgentConnectionSettings>(new AgentConnectionSettings(connectionsOn, 5577, null)),
                new State<AgentConnectionState>(new AgentConnectionState.Off()),
                new QueuedDispatcher()),
            localization);
}

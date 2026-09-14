using GitBench.Features.Repos;
using GitBench.Messages;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Tests;

// The index-operations store a LocalChangesViewModel under test projects from, already following
// the registry the way the app's hosted-service start would have it.
internal static class StartedIndexOperationsStore
{
    public static RepoIndexOperationsStore Create(IRepoRegistry registry, IMessageBus bus, ILocalizationService loc, IUiDispatcher dispatcher)
    {
        var store = new RepoIndexOperationsStore(registry, bus, loc, dispatcher);
        store.Start();
        return store;
    }
}

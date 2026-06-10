using GitBench.Controls;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.App;

internal static class AppServices
{
    public static void AddAppServices(
        this Context context,
        PreferencesService preferences,
        IdentityProfileService identityProfiles,
        string statePath)
    {
        context.AddService(preferences);
        context.AddService(identityProfiles);

        var messageBus = new MessageBus();
        context.AddService<IMessageBus>(messageBus);
        context.AddService(new State<MainViewMode>(MainViewMode.LocalChanges));

        var themeMode = new State<ThemeMode>(preferences.Current.Theme);
        themeMode.Changed += preferences.SetTheme;
        context.AddService(themeMode);
        context.AddService<IThemeService<ThemeStyles>>(new ThemeService(themeMode));

        context.AddPlatformServices();

        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        context.AddService<IRepoRegistry>(registry);
        var repoActivity = new RepoActivityTracker();
        context.AddService<IRepoActivityTracker>(repoActivity);
        var gitService = new GitService(repoActivity);
        context.AddService<IGitService>(gitService);
        // Build the identity resolver (it reads config through gitService) then attach it back, so every
        // git invocation gets the right per-repo name/email/SSH key injected without touching repo config.
        var identityService = new GitIdentityService(gitService, identityProfiles, messageBus, registry);
        context.AddService(identityService);
        gitService.AttachIdentityResolver(identityService);
        context.AddService<IDragController>(new DragController(registry));
        context.AddService(new LocalChangesSelectionStore());

        context.AddService(new UpdateService());
    }
}

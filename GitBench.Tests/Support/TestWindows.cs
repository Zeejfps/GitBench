using GitBench.App;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Tests;

// The review-window registry sits at the end of a chain of app singletons (local changes, diff
// windows); tests that only need the registry build the whole chain through here.
internal static class TestWindows
{
    public static ReviewWindowsViewModel Review(
        IMessageBus bus,
        IReviewStackSource source,
        IRepoRegistry registry,
        GitService git,
        ISymbolExtractor extractor,
        IRepoSnapshotStore snapshots,
        IReviewProgressStore progress,
        IUiDispatcher dispatcher,
        ILocalizationService loc,
        PreferencesService preferences)
    {
        var shell = new FakeShell();
        var localChanges = new LocalChangesViewModel(
            registry, git, git, git, git, git, dispatcher, new FrameTicker(), bus,
            new IdleIndexOperations(), new LocalChangesSelectionStore(), shell, new FakeClipboard(),
            preferences, snapshots, new NoStatusIngest(), loc, new NoUnsavedEdits());
        var diffWindows = new DiffWindowsViewModel(
            registry, git, git, git, extractor, new PlainText(), dispatcher, bus, loc, localChanges, shell);
        return new ReviewWindowsViewModel(
            bus, source, registry, git, git, git, git, git, extractor, new PlainText(),
            snapshots, progress, dispatcher, loc, preferences, localChanges, diffWindows, shell);
    }
}

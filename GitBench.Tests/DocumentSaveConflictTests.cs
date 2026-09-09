using System.Collections.Concurrent;
using System.Text;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>What a save does about a file git still has unmerged.</summary>
public sealed class DocumentSaveConflictTests : IDisposable
{
    private readonly ConflictedRepo _merge = ConflictedRepo.Merging();
    private readonly GitService _git = new(new RepoActivityTracker());
    private readonly TempDir _state = new("gitbench-save-state-");
    private readonly MessageBus _bus = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));

    public void Dispose()
    {
        _loc.Dispose();
        _merge.Dispose();
        _state.Dispose();
    }

    [Fact]
    public void SavingAConflictedFileOffersToMarkItResolvedAndDoesNotDoSoUnasked()
    {
        var conflicts = new RecordingConflicts(_git);
        var offers = Subscribe();
        var path = Path.Combine(_merge.Path, "a.txt");

        Assert.Equal(DocumentSave.Ok, Save(conflicts, path, "one\nmine and theirs\nthree\n"));

        Assert.Single(WaitForOffers(offers));
        Assert.Equal("one\nmine and theirs\nthree\n", File.ReadAllText(path));
        Assert.Empty(conflicts.Resolved);
        Assert.Contains(_git.GetConflictedPaths(_merge.Repo), c => c.Path == "a.txt");
    }

    [Fact]
    public void SavingAFileThatIsNotConflictedAsksNothing()
    {
        var conflicts = new RecordingConflicts(_git);
        var offers = Subscribe();
        var path = Path.Combine(_merge.Path, "quiet.txt");

        Save(conflicts, path, "still nobody's business\n");

        Assert.Empty(WaitForOffers(offers, expected: 0));
        Assert.Empty(conflicts.Resolved);
    }

    [Fact]
    public void AFailedWriteIsReportedAndAsksNothingAboutTheConflict()
    {
        var conflicts = new RecordingConflicts(_git);
        var errors = new List<ShowOperationErrorMessage>();
        _bus.Subscribe<ShowOperationErrorMessage>(errors.Add);
        var offers = Subscribe();
        var path = Path.Combine(_merge.Path, "a.txt");
        var (document, encoding) = Read(path);

        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.IsType<DocumentSave.Failed>(Saver(conflicts).Save(path, document, encoding));

        Assert.Single(errors);
        Assert.Equal(_loc.Strings.Value.EditorSaveFailedTitle, errors[0].Title);
        Assert.Empty(WaitForOffers(offers, expected: 0));
    }

    [Fact]
    public void MarkingResolvedStagesTheFileTheSaveWrote()
    {
        var conflicts = new RecordingConflicts(_git);
        var path = Path.Combine(_merge.Path, "a.txt");
        Save(conflicts, path, "one\nmine and theirs\nthree\n");

        var vm = new MarkResolvedDialogViewModel(
            _merge.Repo, "a.txt", conflicts, _dispatcher, _bus, _loc);
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.MarkResolved.Execute();
        Settle(() => !vm.MarkResolved.IsRunning.Value);

        Assert.True(closed);
        Assert.Equal(["a.txt"], conflicts.Resolved);
        Assert.DoesNotContain(_git.GetConflictedPaths(_merge.Repo), c => c.Path == "a.txt");
    }

    private DocumentSave Save(IGitConflictOperations conflicts, string path, string contents)
    {
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents));
        var (document, encoding) = Read(path);
        return Saver(conflicts).Save(path, document, encoding);
    }

    private static (TextDocument Document, FileEncoding Encoding) Read(string path)
    {
        var preview = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));
        return (
            TextDocument.FromText(preview.Lines.Text),
            Assert.IsType<FileWriteBack.Reversible>(preview.WriteBack).Encoding);
    }

    private DocumentSaves Saver(IGitConflictOperations conflicts)
    {
        var statePath = Path.Combine(_state.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        registry.Open(_merge.Path);
        return new DocumentSaves(
            new TestDocuments.Empty(), conflicts, registry, _bus, _dispatcher, _loc);
    }

    private List<ShowDialogMessage> Subscribe()
    {
        var offers = new List<ShowDialogMessage>();
        _bus.Subscribe<ShowDialogMessage>(offers.Add);
        return offers;
    }

    private List<ShowDialogMessage> WaitForOffers(List<ShowDialogMessage> offers, int expected = 1)
    {
        Settle(() => offers.Count >= expected, seconds: expected == 0 ? 1 : 30);
        return offers;
    }

    private void Settle(Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            _dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(5);
        }

        _dispatcher.Drain();
    }

    private sealed class RecordingConflicts(IGitConflictOperations inner) : IGitConflictOperations
    {
        public List<string> Resolved { get; } = [];

        public GitOutcome TakeOurs(Repo repo, string path) => inner.TakeOurs(repo, path);
        public GitOutcome TakeTheirs(Repo repo, string path) => inner.TakeTheirs(repo, path);
        public GitOutcome TakeBoth(Repo repo, string path) => inner.TakeBoth(repo, path);

        public GitOutcome MarkResolved(Repo repo, string path)
        {
            Resolved.Add(path);
            return inner.MarkResolved(repo, path);
        }

        public ConflictContext? GetConflictContext(Repo repo, string path) =>
            inner.GetConflictContext(repo, path);

        public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo) =>
            inner.GetConflictedPaths(repo);

        public ConflictStages? GetConflictStages(Repo repo, string path) =>
            inner.GetConflictStages(repo, path);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }
}

using GitBench.Features.Editor;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>What a git operation about to rewrite the working tree owes a file being typed into: the chance to say no.</summary>
public sealed class UnsavedEditsGuardTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-unsaved-guard-");
    private readonly MessageBus _bus = new();
    private readonly List<ShowDialogMessage> _dialogs = [];

    private RepoRegistry _registry = null!;
    private DocumentStore _documents = null!;
    private Repo _repo;

    public UnsavedEditsGuardTests()
    {
        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _documents = new DocumentStore(_registry, new LocalizationService(new State<Locale>(Locale.En)));
        _repo = new Repo(Guid.NewGuid(), _dir.Path, "repo");
        _bus.Subscribe<ShowDialogMessage>(_dialogs.Add);
    }

    public void Dispose()
    {
        _documents.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void NothingUnsavedRunsTheOperationOutright()
    {
        var ran = false;
        Guard().Guard(new OverwriteScope.WorkingTree(_repo.Id), () => ran = true);

        Assert.True(ran);
        Assert.Empty(_dialogs);
    }

    [Fact]
    public void DiscardingAFileBeingTypedIntoAsksFirst()
    {
        Type("notes.md", "one");

        var ran = false;
        Guard().Guard(
            new OverwriteScope.Files(_repo.Id, [Path.Combine(_dir.Path, "notes.md")]),
            () => ran = true);

        Assert.False(ran);
        Assert.Single(_dialogs);
    }

    [Fact]
    public void AWorkingTreeOperationAsksAboutEveryUnsavedFileInIt()
    {
        Type("notes.md", "one");
        Type("other.md", "two");

        Guard().Guard(new OverwriteScope.WorkingTree(_repo.Id), () => { });

        Assert.Single(_dialogs);
    }

    [Fact]
    public void AnotherRepositorysUnsavedFileIsNotAskedAbout()
    {
        Type("notes.md", "one");

        var ran = false;
        Guard().Guard(new OverwriteScope.WorkingTree(Guid.NewGuid()), () => ran = true);

        Assert.True(ran);
        Assert.Empty(_dialogs);
    }

    [Fact]
    public void AFileNotNamedByTheOperationIsNotAskedAbout()
    {
        Type("notes.md", "one");

        var ran = false;
        Guard().Guard(
            new OverwriteScope.Files(_repo.Id, [Path.Combine(_dir.Path, "other.md")]),
            () => ran = true);

        Assert.True(ran);
        Assert.Empty(_dialogs);
    }

    private IUnsavedEditsGuard Guard() => new UnsavedEditsGuard(_documents, _registry, _bus);

    private void Type(string name, string text)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, text);
        var buffer = _documents.For(_repo.Id).Open(
            path,
            new FileText(text),
            new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, EndsWithNewline: false)),
            null);
        buffer!.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");
    }
}

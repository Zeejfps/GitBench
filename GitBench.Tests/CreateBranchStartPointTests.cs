using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// "Branch from where I am" used to be a branch name the UI read at some earlier moment, which is how
// a branch could be created off the branch the user had just left. GitRef makes the two intents
// different types: GitRef.Head reaches git as the literal HEAD and resolves inside the command, under
// the repo lock; GitRef.Named is a ref the caller means literally.
public sealed class CreateBranchStartPointTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly CountingGitService _git = new(new GitService(new RepoActivityTracker()));
    private readonly RepoHeadStore _head;
    private readonly string _repoPath;
    private readonly Repo _repo;

    public CreateBranchStartPointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-startpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _head = new RepoHeadStore(_git, _bus, _loc, _dispatcher);

        // main holds one commit; feature branches off it and adds a second, so "which branch did this
        // start from" is answerable by comparing SHAs.
        _repoPath = Path.Combine(_root, "solo");
        Directory.CreateDirectory(_repoPath);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "0");
        Git("add", "a.txt");
        Git("commit", "-qm", "base");
        Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "1");
        Git("commit", "-qam", "on feature");
        Git("checkout", "-q", "main");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_repoPath));
        _repo = _registry.Repos.Single();
    }

    // The reported bug, reproduced at the seam: a name read before the switch still names the branch
    // being left, and creating from that name lands on the wrong base. GitRef.Head cannot, because it
    // isn't resolved until git runs it.
    [Fact]
    public void A_name_read_before_a_switch_is_stale_where_GitRef_Head_is_not()
    {
        // Exactly what the toolbar used to capture: the current branch, read before switching.
        var nameReadBeforeTheSwitch = "main";

        _head.Checkout(_repo, "feature");
        DrainUntil(() => !_head.For(_repo.Id).IsMoving, "the checkout to land");

        _git.CreateBranch(_repo, "from-head", GitRef.Head, checkout: false);
        _git.CreateBranch(_repo, "from-captured-name", GitRef.Named(nameReadBeforeTheSwitch), checkout: false);

        Assert.Equal(Sha("feature"), Sha("from-head"));
        // The old behavior, kept as the contrast: a captured name is still that branch, whatever HEAD
        // has done since — which is right for a ref the user picked, and wrong for "where I am".
        Assert.Equal(Sha("main"), Sha("from-captured-name"));
    }

    // The dialog shows a branch name so the field reads well, but an untouched field must still send
    // HEAD — the label is orientation, not the argument.
    [Fact]
    public void An_untouched_start_point_field_sends_HEAD_however_it_is_labelled()
    {
        using var vm = NewDialog(GitRef.Head, label: "main");
        vm.Name.Value = "created";

        Execute(vm);

        Assert.Equal("HEAD", _git.LastCreateBranchStartPoint?.Argument);
    }

    // Clearing it means HEAD too — that is what the field's hint promises.
    [Fact]
    public void A_cleared_start_point_field_sends_HEAD()
    {
        using var vm = NewDialog(GitRef.Head, label: "main");
        vm.Name.Value = "created";
        vm.StartPoint.Value = "";

        Execute(vm);

        Assert.Equal("HEAD", _git.LastCreateBranchStartPoint?.Argument);
    }

    // Typing a ref means that ref literally — the escape hatch stays open.
    [Fact]
    public void An_edited_start_point_field_sends_what_was_typed()
    {
        using var vm = NewDialog(GitRef.Head, label: "main");
        vm.Name.Value = "created";
        vm.StartPoint.Value = "feature";

        Execute(vm);

        Assert.Equal("feature", _git.LastCreateBranchStartPoint?.Argument);
        Assert.Equal(Sha("feature"), Sha("created"));
    }

    // Opened from a commit row, the start point is that commit and must stay pinned to it — this is
    // the case GitRef.Head would be wrong for.
    [Fact]
    public void A_dialog_opened_at_a_commit_keeps_that_commit_as_the_start_point()
    {
        var sha = Sha("feature");
        using var vm = NewDialog(GitRef.Named(sha), label: sha);
        vm.Name.Value = "created";

        Execute(vm);

        Assert.Equal(sha, _git.LastCreateBranchStartPoint?.Argument);
        Assert.Equal(sha, Sha("created"));
    }

    // ---- helpers ----

    // checkout: false throughout — these tests are about the start point, not the switch.
    private CreateBranchDialogViewModel NewDialog(GitRef startPoint, string label)
    {
        var vm = new CreateBranchDialogViewModel(
            _repo, startPoint, label, initialName: "",
            _git, _dispatcher, _bus, _head, _loc);
        vm.Checkout.Value = false;
        return vm;
    }

    private void Execute(CreateBranchDialogViewModel vm)
    {
        vm.Create.Execute();
        DrainUntil(() => !vm.Create.IsRunning.Value, "the create to report");
        Assert.Null(vm.Create.Error.Value);
    }

    private string Sha(string rev) => RunGit("rev-parse", rev).Trim();

    private void Git(params string[] args) => RunGit(args);

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    private void DrainUntil(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            _dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public void Dispose()
    {
        _head.Dispose();
        _registry.Dispose();
        _loc.Dispose();
        DirectoryTree.Delete(_root);
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

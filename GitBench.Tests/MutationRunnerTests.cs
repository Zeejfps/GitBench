using System.Diagnostics;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// Mutations and loads have opposite staleness semantics. A newer load supersedes an older one — both
// answer the same question and the fresher answer wins. A newer mutation supersedes nothing: the git
// process already ran, the index already moved, and the continuation is the sole carrier of the
// error message and of the broadcast that reconciles the optimistically-updated lists. These pin
// both halves, plus the revalidation the runner owes on every path out.
public sealed class MutationRunnerTests : IDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly Channels _seen;
    private readonly ProbeViewModel _vm;
    private readonly Guid _repoId = Guid.NewGuid();
    private readonly List<ManualResetEventSlim> _gates = new();

    public MutationRunnerTests()
    {
        _seen = new Channels(_bus);
        _vm = new ProbeViewModel(_dispatcher);
    }

    // The defect this whole item exists for: a stage and a submodule reset shared one generation
    // lane, so whichever finished first had its result thrown away — no error, no broadcast, and the
    // file panel wedged on its optimistic state.
    [Fact]
    public void A_slower_mutation_still_delivers_after_a_newer_one_has_landed()
    {
        var gate = Gate();

        _vm.Mutate("slow", Index(), () => { gate.Wait(); return GitOutcome.Ok; });
        _vm.Mutate("fast", WorkingTree(), () => GitOutcome.Ok);
        Pump.WaitFor(_dispatcher, () => _vm.Delivered.Contains("fast"), "the newer mutation");

        gate.Set();
        Pump.WaitFor(_dispatcher, () => _vm.Delivered.Contains("slow"), "the older mutation's result");
        Assert.Equal(2, _seen.Count);
    }

    [Fact]
    public void A_failed_mutation_delivers_its_error_and_still_broadcasts()
    {
        _vm.Mutate("op", WorkingTree(), () => new GitOutcome.Failed("index.lock is held"));

        Pump.WaitFor(_dispatcher, () => _vm.Delivered.Count == 1, "the failure");
        Assert.Equal("index.lock is held", _vm.LastError);
        Assert.Equal(new[] { $"working-tree:{_repoId}" }, _seen.Order);
    }

    [Fact]
    public void A_thrown_exception_becomes_a_failed_outcome_and_still_broadcasts()
    {
        _vm.Mutate("op", WorkingTree(), () => throw new IOException("git vanished"));

        Pump.WaitFor(_dispatcher, () => _vm.Delivered.Count == 1, "the folded failure");
        Assert.Equal("git vanished", _vm.LastError);
        Assert.Equal(1, _seen.Count);
    }

    // The broadcast lives in a finally precisely so a continuation bug cannot cost the app its
    // revalidation.
    [Fact]
    public void A_throwing_continuation_cannot_swallow_the_broadcast()
    {
        _vm.MutateThenThrow(WorkingTree(), () => GitOutcome.Ok);
        WaitUntilPosted();

        Assert.Throws<InvalidOperationException>(_dispatcher.Drain);
        Assert.Equal(1, _seen.Count);
    }

    // The revalidation is owed to the stores, not to the panel that started the op — a disposed view
    // model must not take the rest of the app's view of the repo down with it.
    [Fact]
    public void Disposal_drops_the_continuation_but_not_the_broadcast()
    {
        var gate = Gate();
        _vm.Mutate("op", WorkingTree(), () => { gate.Wait(); return GitOutcome.Ok; });

        _vm.Dispose();
        gate.Set();

        Pump.WaitFor(_dispatcher, () => _seen.Count == 1, "the broadcast after disposal");
        Assert.Empty(_vm.Delivered);
    }

    [Fact]
    public void An_index_mutation_carries_the_index_only_flag_and_its_path()
    {
        _vm.Mutate("op", MutationEffects.Index(_bus, _repoId, "src/a.txt"), () => GitOutcome.Ok);

        Pump.WaitFor(_dispatcher, () => _seen.Count == 1, "the index broadcast");
        var msg = Assert.Single(_seen.WorkingTree);
        Assert.True(msg.IndexOnly);
        Assert.Equal("src/a.txt", msg.Path);
    }

    [Fact]
    public void A_working_tree_mutation_is_not_index_only()
    {
        _vm.Mutate("op", WorkingTree(), () => GitOutcome.Ok);

        Pump.WaitFor(_dispatcher, () => _seen.Count == 1, "the working-tree broadcast");
        var msg = Assert.Single(_seen.WorkingTree);
        Assert.False(msg.IndexOnly);
        Assert.Null(msg.Path);
    }

    // The working-tree broadcast releases LocalChangesViewModel's optimistic hold, so it goes last:
    // every other store has already been told to reload by the time the lists start accepting
    // snapshots again.
    [Fact]
    public void Extra_channels_are_broadcast_before_the_working_tree()
    {
        var primaryId = Guid.NewGuid();
        _vm.Mutate("op", WorkingTree().AndRefs().AndSubmodulesOf(primaryId), () => GitOutcome.Ok);

        Pump.WaitFor(_dispatcher, () => _seen.Count == 3, "all three channels");
        Assert.Equal(
            new[] { $"refs:{_repoId}", $"submodules:{primaryId}", $"working-tree:{_repoId}" },
            _seen.Order);
    }

    [Fact]
    public void A_commit_broadcasts_the_commit_channel_instead_of_the_working_tree()
    {
        _vm.Mutate("op", MutationEffects.Commit(_bus, _repoId), () => GitOutcome.Ok);

        Pump.WaitFor(_dispatcher, () => _seen.Count == 1, "the commit broadcast");
        Assert.Equal(new[] { $"commit:{_repoId}" }, _seen.Order);
    }

    // A rejected commit needs the same revalidation: a pre-commit hook can reformat the working tree
    // and then fail.
    [Fact]
    public void A_rejected_commit_still_broadcasts()
    {
        _vm.Mutate("op", MutationEffects.Commit(_bus, _repoId), () => new GitOutcome.Failed("hook refused"));

        Pump.WaitFor(_dispatcher, () => _seen.Count == 1, "the broadcast after a rejected commit");
        Assert.Equal("hook refused", _vm.LastError);
    }

    // The other half of the split: loads must keep superseding, or the amend head-files refresh
    // starts applying answers to questions nobody is asking any more.
    [Fact]
    public void A_newer_load_still_supersedes_an_older_one()
    {
        var gate = Gate();

        _vm.Load("slow", () => { gate.Wait(); return "slow"; });
        _vm.Load("fast", () => "fast");
        Pump.WaitFor(_dispatcher, () => _vm.Delivered.Contains("fast"), "the newer load");

        gate.Set();
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(400));
        Assert.Equal(new[] { "fast" }, _vm.Delivered);
    }

    // ---- helpers ----

    private MutationEffects Index(string? path = null) => MutationEffects.Index(_bus, _repoId, path);

    private MutationEffects WorkingTree() => MutationEffects.WorkingTree(_bus, _repoId);

    private ManualResetEventSlim Gate()
    {
        var gate = new ManualResetEventSlim(false);
        _gates.Add(gate);
        return gate;
    }

    private void WaitUntilPosted()
    {
        var sw = Stopwatch.StartNew();
        while (_dispatcher.Queued == 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
        Assert.True(_dispatcher.Queued > 0, "the continuation was never posted");
    }

    public void Dispose()
    {
        foreach (var gate in _gates)
        {
            gate.Set();
            gate.Dispose();
        }
        _vm.Dispose();
    }

    // Records what was broadcast and in what order, so a test can assert on the sequence rather than
    // on per-channel counters.
    private sealed class Channels
    {
        public readonly List<string> Order = new();
        public readonly List<WorkingTreeChangedMessage> WorkingTree = new();

        public Channels(IMessageBus bus)
        {
            bus.Subscribe<WorkingTreeChangedMessage>(m =>
            {
                WorkingTree.Add(m);
                Order.Add($"working-tree:{m.RepoId}");
            });
            bus.Subscribe<RefsChangedMessage>(m => Order.Add($"refs:{m.RepoId}"));
            bus.Subscribe<SubmodulesChangedMessage>(m => Order.Add($"submodules:{m.PrimaryRepoId}"));
            bus.Subscribe<CommitCreatedMessage>(m => Order.Add($"commit:{m.RepoId}"));
        }

        public int Count => Order.Count;
    }

    private sealed record ProbeState(string? LastError);

    private sealed class ProbeViewModel : ViewModelBase<ProbeState>
    {
        public readonly List<string> Delivered = new();

        public ProbeViewModel(IUiDispatcher dispatcher) : base(dispatcher, new ProbeState(null)) { }

        public string? LastError => State.Value.LastError;

        public void Mutate(string label, MutationEffects effects, Func<GitOutcome> work)
            => RunMutation(effects, work, outcome =>
            {
                Delivered.Add(label);
                Update(s => s with { LastError = outcome.FailureMessage });
            });

        public void MutateThenThrow(MutationEffects effects, Func<GitOutcome> work)
            => RunMutation(effects, work, _ => throw new InvalidOperationException("continuation blew up"));

        public void Load(string label, Func<string> work)
            => RunBackground<string>(() => (work(), null), (_, _) => Delivered.Add(label));
    }
}

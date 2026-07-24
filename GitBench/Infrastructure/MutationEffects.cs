using GitBench.Messages;

namespace GitBench.Infrastructure;

/// <summary>
/// The revalidation a git mutation owes the rest of the app: which channels it may have moved, and
/// therefore what every other view has to re-read once it returns. Broadcast by
/// <see cref="ViewModelBase{TState}.RunMutation{T}"/> whether the op succeeded or failed — a batch
/// that failed partway still moved the index, a hook that rejected a commit may still have rewritten
/// files, and the panel that fired the op is already painting the optimistic result either way.
///
/// Built through the factory that names what the op did, so a call site cannot describe a mutation
/// that tells nobody. That was the defect: two view models disagreed about whether a failed mutation
/// should broadcast, and the one holding the file lists chose not to — leaving the panel frozen on
/// its optimistic state until something unrelated touched the tree.
/// </summary>
internal sealed class MutationEffects
{
    // The channel that always fires, at the granularity every store keys off. Exactly one, chosen
    // by the factory — there is no "none".
    private enum Channel { Index, WorkingTree, Commit }

    private readonly IMessageBus _bus;
    private readonly Guid _repoId;
    private readonly Channel _channel;
    private readonly string? _path;
    private readonly bool _refs;
    private readonly Guid? _submodulesOfPrimary;

    private MutationEffects(
        IMessageBus bus,
        Guid repoId,
        Channel channel,
        string? path,
        bool refs,
        Guid? submodulesOfPrimary)
    {
        _bus = bus;
        _repoId = repoId;
        _channel = channel;
        _path = path;
        _refs = refs;
        _submodulesOfPrimary = submodulesOfPrimary;
    }

    /// <summary>
    /// Content moved between HEAD and the index with no file on disk changing — a stage, an unstage,
    /// a reset-to-parent. <paramref name="path"/> names the file when the op touched exactly one,
    /// which is what lets a HEAD→disk diff skip a refetch that would return the same bytes.
    /// </summary>
    public static MutationEffects Index(IMessageBus bus, Guid repoId, string? path = null)
        => new(bus, repoId, Channel.Index, path, refs: false, submodulesOfPrimary: null);

    /// <summary>
    /// Files on disk changed — a patch apply, a conflict resolution, a stash, a submodule checkout.
    /// </summary>
    public static MutationEffects WorkingTree(IMessageBus bus, Guid repoId)
        => new(bus, repoId, Channel.WorkingTree, path: null, refs: false, submodulesOfPrimary: null);

    /// <summary>
    /// A commit was attempted. <see cref="CommitCreatedMessage"/> is a revalidate-everything trigger
    /// for every subscriber, which is what a rejected attempt needs too: a pre-commit hook can
    /// reformat the working tree and then fail the commit.
    /// </summary>
    public static MutationEffects Commit(IMessageBus bus, Guid repoId)
        => new(bus, repoId, Channel.Commit, path: null, refs: false, submodulesOfPrimary: null);

    /// <summary>Also moved a ref — a stash push writes <c>refs/stash</c>.</summary>
    public MutationEffects AndRefs()
        => new(_bus, _repoId, _channel, _path, refs: true, _submodulesOfPrimary);

    /// <summary>Also changed the submodules attached to <paramref name="primaryRepoId"/>.</summary>
    public MutationEffects AndSubmodulesOf(Guid primaryRepoId)
        => new(_bus, _repoId, _channel, _path, _refs, primaryRepoId);

    /// <summary>
    /// Refs first, then submodules, then the op's own channel. The working-tree broadcast is what
    /// releases <c>LocalChangesViewModel</c>'s optimistic hold, so it goes last — every other store
    /// has already been told to reload by the time the file lists start accepting snapshots again.
    /// </summary>
    internal void Broadcast()
    {
        if (_refs) _bus.Broadcast(new RefsChangedMessage(_repoId));
        if (_submodulesOfPrimary is { } primaryId) _bus.Broadcast(new SubmodulesChangedMessage(primaryId));
        switch (_channel)
        {
            case Channel.Commit:
                _bus.Broadcast(new CommitCreatedMessage(_repoId));
                break;
            case Channel.Index:
                _bus.Broadcast(new WorkingTreeChangedMessage(_repoId, IndexOnly: true, Path: _path));
                break;
            default:
                _bus.Broadcast(new WorkingTreeChangedMessage(_repoId));
                break;
        }
    }
}

using System.Collections.Concurrent;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using LibGit2Sharp;
using SubmoduleStatus = GitBench.Features.Submodules.SubmoduleStatus;

namespace GitBench.Git;

// Non-injecting config reads the identity resolver needs. Separate from IGitService so the
// resolver depends only on these (and so GitService can hand itself to GitIdentityService
// without a public surface change).
public interface IGitRawConfigReader
{
    // Whether the path is a readable git repo right now. The resolver checks this before spawning
    // any git, so an unmounted/deleted repo resolves as transient instead of being cached wrong.
    bool IsRepoAvailable(string repoPath);
    // Throws on a genuine git failure (e.g. held index.lock) so the resolver can treat it as
    // transient; an empty list means the repo simply has no remotes.
    IReadOnlyList<string> GetRemoteNamesRaw(string repoPath);
    string? GetRemoteUrlRaw(string repoPath, string remoteName);
    (string? Name, string? Email) GetLocalIdentityRaw(string repoPath);

    // Wires the resolver that injects per-repo identity into every git invocation. Called once at
    // startup by GitIdentityService itself (its hosted Start), which needs this reader for its raw
    // config reads — hence the post-construction back-wire rather than a constructor arg either way.
    void AttachIdentityResolver(GitIdentityService identity);
}

public sealed class GitService : IGitService, IGitRawConfigReader
{
    // Every git write touches .git/index.lock; two writes against the same repo at the same
    // time (e.g. a checkout from the sidebar racing a stage from the local-changes panel, or
    // an impatient user double-clicking branches) collide with "Unable to create
    // '.git/index.lock': File exists". Serialize all mutating ops per repo so the call sites
    // can't race each other — their own UI-busy flags become cosmetic, not correctness guards.
    // Reads stay unguarded; libgit2/git CLI tolerate concurrent reads, and the next
    // RefsChangedMessage refresh corrects any brief inconsistency.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // Every git invocation runs through the runner, which opens an activity scope so
    // RepoWatcher can drop the FSW events our own writes cause. Without this the auto-reload
    // loops on its own index/stat-cache mutations. See RepoActivityTracker for the full story.
    private readonly GitProcessRunner _runner;

    public GitService(IRepoActivityTracker activity)
    {
        _runner = new GitProcessRunner(activity);
    }

    private static SemaphoreSlim GetRepoLock(string repoPath)
    {
        string key;
        try { key = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { key = repoPath; }
        return _repoLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    // Acquires the per-repo mutation lock and releases it when the returned scope is disposed.
    // Use with `using` so every mutation path serializes without hand-written try/finally:
    //   using var _ = LockRepo(repo.Path);
    private static RepoLock LockRepo(string repoPath)
    {
        var sem = GetRepoLock(repoPath);
        sem.Wait();
        return new RepoLock(sem);
    }

    private readonly struct RepoLock : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public RepoLock(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }

    // `.git` is a directory in a normal repo and a file in worktrees/submodules. Deeper
    // corruption (missing HEAD, broken objects/) surfaces when the subsequent git command runs.
    private static bool IsGitRepo(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return false;
        var dotGit = Path.Combine(repoPath, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }


    public Fetched<CommitSnapshot> Load(Repo repo, int cap)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<CommitSnapshot>.Failed("Not a git repository.");

            using var lg = new Repository(repo.Path);

            var head = ReadHead(lg);
            var scan = ScanRefs(lg, head);
            var commits = WalkCommits(lg, scan.RefTips, cap, out var truncated);

            var (indexBySha, parentShasByIndex) = BuildParentIndex(commits);
            // Remote-only = displayed but not reachable from any local tip. Anchored widens the
            // seeds with the matched remote tips, so !anchored = reachable only from remote
            // branches with no local counterpart (what the history view's remote filter hides).
            var localReachable = ComputeReachability(indexBySha, parentShasByIndex, scan.LocalTips);
            var anchoredReachable = ComputeReachability(indexBySha, parentShasByIndex, scan.LocalTips.Concat(scan.MatchedRemoteTips));

            var inputs = BuildLaneInputs(commits, parentShasByIndex, localReachable, scan.BadgesBySha);
            var (assignments, laneCount) = LaneAssigner.Assign(inputs);
            var nodes = BuildNodes(commits, inputs, assignments, scan.BadgesBySha, localReachable, anchoredReachable);

            return new CommitSnapshot(repo.Id, repo.Path, nodes, laneCount, truncated, head.BranchName);
        }
        catch (Exception ex)
        {
            return new Fetched<CommitSnapshot>.Failed(ex.Message);
        }
    }

    // BranchName is null when HEAD is detached; otherwise the checked-out branch's friendly name.
    private readonly record struct HeadState(Commit? Tip, string? Sha, bool IsDetached, string? BranchName);

    // Ref tips and badges gathered from a repo's branches, HEAD, stashes, and tags.
    private sealed class RefScan
    {
        // Seeds for the topological commit walk (all displayed refs).
        public readonly List<Commit> RefTips = new();
        // Tips reachable purely from local refs (branches, HEAD, tags, stashes). A displayed
        // commit not reachable from any of these is remote-only.
        public readonly List<Commit> LocalTips = new();
        // Remote tips anchored by a local counterpart; widen the "not remote-only" set.
        public readonly List<Commit> MatchedRemoteTips = new();
        public readonly Dictionary<string, List<RefBadge>> BadgesBySha = new();
        // Remote branches folded into a local branch's synced badge; skipped in the remote pass.
        public readonly HashSet<string> AbsorbedRemotes = new(StringComparer.Ordinal);
    }

    private static HeadState ReadHead(Repository lg)
    {
        var tip = lg.Head?.Tip;
        var isDetached = lg.Info.IsHeadDetached;
        return new HeadState(tip, tip?.Sha, isDetached, isDetached ? null : lg.Head?.FriendlyName);
    }

    private static RefScan ScanRefs(Repository lg, HeadState head)
    {
        var scan = new RefScan();
        var (localBranches, remoteBranches) = SplitBranches(lg, scan);
        CollectMatchedRemoteTips(localBranches, remoteBranches, scan);
        AddLocalBranchBadges(localBranches, remoteBranches, head, scan);
        AddRemoteBranchBadges(remoteBranches, scan);
        AddDetachedHeadBadge(head, scan);
        AddStashRefs(lg, scan);
        AddTagRefs(lg, scan);
        SeedHeadTip(head, scan);
        return scan;
    }

    // Split branches by kind so the local pass can absorb matching remotes. Drop the remote's
    // symbolic "origin/HEAD" ref — it only ever mirrors the remote's default branch, so it's
    // pure noise next to the branch it points at.
    private static (List<Branch> Local, List<Branch> Remote) SplitBranches(Repository lg, RefScan scan)
    {
        var localBranches = new List<Branch>();
        var remoteBranches = new List<Branch>();
        foreach (var branch in lg.Branches)
        {
            var tip = branch.Tip;
            if (tip == null) continue;
            if (branch.IsRemote)
            {
                if (branch.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                remoteBranches.Add(branch);
            }
            else
            {
                localBranches.Add(branch);
                scan.LocalTips.Add(tip);
            }
            scan.RefTips.Add(tip);
        }
        return (localBranches, remoteBranches);
    }

    // Remote branches anchored by a local counterpart (a local branch tracks them, or a local
    // branch shares their short name). The history view's remote filter keeps commits reachable
    // from these — hiding remote-only branches must not hide e.g. origin/main's unpulled commits
    // while main exists locally.
    private static void CollectMatchedRemoteTips(
        List<Branch> localBranches, List<Branch> remoteBranches, RefScan scan)
    {
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        var trackedUpstreams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in localBranches)
        {
            localNames.Add(local.FriendlyName);
            if (local.TrackedBranch is { } upstream) trackedUpstreams.Add(upstream.FriendlyName);
        }
        foreach (var remote in remoteBranches)
        {
            if (trackedUpstreams.Contains(remote.FriendlyName) || localNames.Contains(RemoteBranchShortName(remote)))
                scan.MatchedRemoteTips.Add(remote.Tip!);
        }
    }

    // A local branch sitting on the same commit as its tracking remote absorbs that remote into a
    // single "synced" badge; the absorbed remote names go into scan.AbsorbedRemotes so the remote
    // pass skips them. The checked-out branch also absorbs the HEAD badge.
    private static void AddLocalBranchBadges(
        List<Branch> localBranches, List<Branch> remoteBranches, HeadState head, RefScan scan)
    {
        foreach (var local in localBranches)
        {
            var tip = local.Tip!;
            var isCurrent = !head.IsDetached && local.FriendlyName == head.BranchName;
            var sync = ResolveBranchSync(local, tip, remoteBranches, scan.AbsorbedRemotes);
            AddBadge(scan.BadgesBySha, tip.Sha,
                new RefBadge(local.FriendlyName, RefKind.LocalBranch, IsCurrent: isCurrent, Sync: sync));
        }
    }

    private static BranchSync ResolveBranchSync(
        Branch local, Commit tip, List<Branch> remoteBranches, HashSet<string> absorbedRemotes)
    {
        var tracked = local.TrackedBranch;
        if (tracked?.Tip != null)
        {
            // Equal tips means neither ahead nor behind — in sync. A divergent upstream lives on a
            // different commit (its own row), so only fold the remote badge in when the two are level.
            var inSync = tracked.Tip.Sha == tip.Sha;
            if (inSync) absorbedRemotes.Add(tracked.FriendlyName);
            return inSync ? BranchSync.InSync : BranchSync.Diverged;
        }

        // No upstream configured (e.g. pushed without -u, or the upstream was later unset). Git
        // records no relationship, but if a remote branch with the conventional "<remote>/<name>"
        // name sits on this exact commit it's effectively the same ref — fold it into one synced
        // badge rather than showing a redundant local/remote pair on the same commit.
        var twin = remoteBranches.FirstOrDefault(r =>
            r.Tip!.Sha == tip.Sha && RemoteBranchShortName(r) == local.FriendlyName);
        if (twin == null) return BranchSync.Untracked;
        absorbedRemotes.Add(twin.FriendlyName);
        return BranchSync.InSync;
    }

    private static void AddRemoteBranchBadges(List<Branch> remoteBranches, RefScan scan)
    {
        foreach (var remote in remoteBranches)
        {
            if (scan.AbsorbedRemotes.Contains(remote.FriendlyName)) continue;
            AddBadge(scan.BadgesBySha, remote.Tip!.Sha, new RefBadge(remote.FriendlyName, RefKind.RemoteBranch));
        }
    }

    // HEAD only gets its own badge when detached; otherwise it's represented by the current
    // branch's badge (IsCurrent).
    private static void AddDetachedHeadBadge(HeadState head, RefScan scan)
    {
        if (head.Sha != null && head.IsDetached)
            AddBadge(scan.BadgesBySha, head.Sha, new RefBadge("HEAD", RefKind.Head));
    }

    // Walk stash tips too so stash commits show in the graph. Stash entries are merge commits whose
    // parents include the index/untracked snapshots — those get pulled in via the topological walk.
    private static void AddStashRefs(Repository lg, RefScan scan)
    {
        var stashIndex = 0;
        foreach (var stash in lg.Stashes)
        {
            var tip = stash.WorkTree;
            if (tip == null) { stashIndex++; continue; }
            scan.RefTips.Add(tip);
            scan.LocalTips.Add(tip);
            var label = StripStashPrefix(stash.Message ?? string.Empty);
            if (string.IsNullOrEmpty(label)) label = $"stash@{{{stashIndex}}}";
            AddBadge(scan.BadgesBySha, tip.Sha, new RefBadge(label, RefKind.Stash));
            stashIndex++;
        }
    }

    // Tags peel to the commit they ultimately reference (annotated tags point at a tag object,
    // lightweight ones directly at the commit). Adding the commit as a ref tip keeps tagged
    // history reachable even when no branch points at it.
    private static void AddTagRefs(Repository lg, RefScan scan)
    {
        foreach (var tag in lg.Tags)
        {
            if (tag.PeeledTarget is not Commit tagged) continue;
            scan.RefTips.Add(tagged);
            scan.LocalTips.Add(tagged);
            AddBadge(scan.BadgesBySha, tagged.Sha, new RefBadge(tag.FriendlyName, RefKind.Tag));
        }
    }

    // Always seed the walk from HEAD. On a branch this tip is already in RefTips and libgit2
    // dedupes by reachability, so it's a no-op. When detached, HEAD's commits may be reachable
    // from no other ref (ahead of every branch) — without this they'd be silently excluded from
    // the graph, making committed work look lost.
    private static void SeedHeadTip(HeadState head, RefScan scan)
    {
        if (head.Tip == null) return;
        scan.RefTips.Add(head.Tip);
        scan.LocalTips.Add(head.Tip);
    }

    private static List<Commit> WalkCommits(Repository lg, List<Commit> refTips, int cap, out bool truncated)
    {
        var filter = new CommitFilter
        {
            IncludeReachableFrom = refTips,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };

        var commits = new List<Commit>(cap);
        truncated = false;
        foreach (var c in lg.Commits.QueryBy(filter))
        {
            if (commits.Count >= cap)
            {
                truncated = true;
                break;
            }
            commits.Add(c);
        }
        return commits;
    }

    private static (Dictionary<string, int> IndexBySha, string[][] ParentShasByIndex) BuildParentIndex(
        List<Commit> commits)
    {
        var indexBySha = new Dictionary<string, int>(commits.Count);
        var parentShasByIndex = new string[commits.Count][];
        for (var i = 0; i < commits.Count; i++)
        {
            indexBySha[commits[i].Sha] = i;
            parentShasByIndex[i] = commits[i].Parents.Select(p => p.Sha).ToArray();
        }
        return (indexBySha, parentShasByIndex);
    }

    // A commit is auxiliary (off the main lane graph) when it's remote-only or a stash tip.
    private static LaneAssigner.Input[] BuildLaneInputs(
        List<Commit> commits, string[][] parentShasByIndex, bool[] localReachable,
        Dictionary<string, List<RefBadge>> badgesBySha)
    {
        var inputs = new LaneAssigner.Input[commits.Count];
        for (var i = 0; i < commits.Count; i++)
        {
            var sha = commits[i].Sha;
            var auxiliary = !localReachable[i] || HasStashBadge(badgesBySha, sha);
            inputs[i] = new LaneAssigner.Input(sha, parentShasByIndex[i], auxiliary);
        }
        return inputs;
    }

    private static bool HasStashBadge(Dictionary<string, List<RefBadge>> badgesBySha, string sha)
    {
        if (!badgesBySha.TryGetValue(sha, out var badges)) return false;
        foreach (var b in badges)
            if (b.Kind == RefKind.Stash) return true;
        return false;
    }

    private static CommitNode[] BuildNodes(
        List<Commit> commits, LaneAssigner.Input[] inputs, LaneAssignment[] assignments,
        Dictionary<string, List<RefBadge>> badgesBySha, bool[] localReachable, bool[] anchoredReachable)
    {
        var nodes = new CommitNode[commits.Count];
        for (var i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            var a = assignments[i];
            badgesBySha.TryGetValue(c.Sha, out var badges);

            nodes[i] = new CommitNode(
                Sha: c.Sha,
                Summary: c.MessageShort ?? string.Empty,
                Author: c.Author?.Name ?? string.Empty,
                When: c.Author?.When ?? c.Committer?.When ?? DateTimeOffset.MinValue,
                ParentShas: (IReadOnlyList<string>)inputs[i].ParentShas,
                Lane: a.Lane,
                HasIncomingAtCommitLane: a.HasIncomingAtCommitLane,
                IncomingAtCommitLaneDashed: a.IncomingAtCommitLaneDashed,
                InWalkParentLanes: MapParentLinks(a),
                IncomingLanes: a.IncomingLanes,
                PassThroughLanes: a.PassThroughLanes,
                Refs: badges ?? (IReadOnlyList<RefBadge>)Array.Empty<RefBadge>(),
                RemoteOnly: !localReachable[i],
                UnmatchedRemoteOnly: !anchoredReachable[i]);
        }
        return nodes;
    }

    private static ParentLink[] MapParentLinks(LaneAssignment a)
    {
        var links = new ParentLink[a.InWalkParentLanes.Length];
        for (var k = 0; k < links.Length; k++)
        {
            var p = a.InWalkParentLanes[k];
            links[k] = new ParentLink(p.ParentIndex, p.Lane);
        }
        return links;
    }

    // Marks every commit reachable from the seed tips. The walk is topologically sorted (a
    // commit precedes its parents), so a single forward pass propagates the mark to ancestors.
    private static bool[] ComputeReachability(
        Dictionary<string, int> indexBySha, string[][] parentShasByIndex, IEnumerable<Commit> seeds)
    {
        var reachable = new bool[parentShasByIndex.Length];
        foreach (var seed in seeds)
            if (indexBySha.TryGetValue(seed.Sha, out var si))
                reachable[si] = true;
        for (var i = 0; i < parentShasByIndex.Length; i++)
        {
            if (!reachable[i]) continue;
            foreach (var parent in parentShasByIndex[i])
                if (indexBySha.TryGetValue(parent, out var pi))
                    reachable[pi] = true;
        }
        return reachable;
    }

    // Lists base..head as a linear review stack for the review window (decisions #3/#6). Mirrors
    // Load's RevWalk but with a range + first-parent filter: the commits reachable from head but
    // not base, walked newest→oldest, then reversed so the stack reads base→tip. base/head accept
    // any ref or SHA; the returned stack carries their resolved SHAs and short-sha labels (the
    // caller overrides labels with branch names). Each increment's churn is the commit-vs-first-parent
    // line counts (the unit the review pane shows).
    public Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<ReviewStack>.Failed("Not a git repository.");

            using var lg = new Repository(repo.Path);

            var headCommit = lg.Lookup<Commit>(headRef);
            if (headCommit == null)
                return new Fetched<ReviewStack>.Failed($"Could not resolve '{headRef}'.");
            var baseCommit = lg.Lookup<Commit>(baseRef);
            if (baseCommit == null)
                return new Fetched<ReviewStack>.Failed($"Could not resolve '{baseRef}'.");

            var filter = new CommitFilter
            {
                IncludeReachableFrom = headCommit,
                ExcludeReachableFrom = baseCommit,
                FirstParentOnly = true,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            };

            // Churn for the rows' "+N −M": commit-vs-first-parent counts, fetched for the whole
            // range in one `git log --numstat` pass. Not Compare<Patch> — like GetDiff, patch
            // building would go through the callback marshalling path that crashes NativeAOT.
            var churnBySha = LoadRangeChurn(repo.Path, baseCommit.Sha, headCommit.Sha, cap);

            var increments = new List<ReviewIncrement>();
            var truncated = false;
            foreach (var c in lg.Commits.QueryBy(filter))
            {
                if (increments.Count >= cap)
                {
                    truncated = true;
                    break;
                }

                churnBySha.TryGetValue(c.Sha, out var churn);
                increments.Add(new ReviewIncrement(
                    c.Sha,
                    ShortSha(c.Sha),
                    c.MessageShort ?? string.Empty,
                    c.Author?.Name ?? string.Empty,
                    c.Author?.When ?? c.Committer?.When ?? DateTimeOffset.MinValue,
                    FilesChanged: churn.Files, Added: churn.Added, Removed: churn.Removed));
            }

            increments.Reverse();

            return new ReviewStack(
                repo.Id,
                baseCommit.Sha,
                headCommit.Sha,
                ShortSha(baseCommit.Sha),
                ShortSha(headCommit.Sha),
                increments,
                truncated);
        }
        catch (Exception ex)
        {
            return new Fetched<ReviewStack>.Failed(ex.Message);
        }
    }

    // Per-commit churn for base..head from a single `git log --numstat` invocation: each commit's
    // file count and added/removed line totals, keyed by SHA. A commit absent from the output (or
    // past the cap under a different ordering) reads as zero churn. Binary files contribute a file
    // count but "-" line counts, which parse as zero; a root commit numstats against the empty tree.
    private Dictionary<string, (int Files, int Added, int Removed)> LoadRangeChurn(
        string repoPath, string baseSha, string headSha, int cap)
    {
        var churn = new Dictionary<string, (int Files, int Added, int Removed)>(StringComparer.Ordinal);
        var output = RunGit(repoPath, out _,
            "log", "--first-parent", "--topo-order", $"--max-count={cap}",
            "--format=%x01%H", "--numstat", "-M", $"{baseSha}..{headSha}");
        if (output == null) return churn;

        string? sha = null;
        var current = (Files: 0, Added: 0, Removed: 0);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '\x01')
            {
                if (sha != null) churn[sha] = current;
                sha = line[1..].Trim();
                current = default;
                continue;
            }
            if (sha == null) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            current.Files++;
            if (int.TryParse(parts[0], out var added)) current.Added += added;
            if (int.TryParse(parts[1], out var removed)) current.Removed += removed;
        }
        if (sha != null) churn[sha] = current;
        return churn;
    }

    public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<IReadOnlyList<FileChange>>.Failed("Not a git repository.");

            // The combined net file list of base→head (two-dot, base is already the merge-base). The
            // `--raw` format additionally carries each file's after-side blob OID, which the review's
            // Viewed marks fingerprint on to re-open a file that changed since it was marked; a file
            // touched in several increments appears once, an add-then-delete nets out.
            var diffOutput = RunGit(repo.Path, out var error, "diff", "-M", "--raw", "-z", baseSha, headSha);
            if (diffOutput == null)
                return new Fetched<IReadOnlyList<FileChange>>.Failed(error ?? "git diff failed.");

            var files = ParseDiffRawZ(diffOutput);
            files.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return new Fetched<IReadOnlyList<FileChange>>.Ok(files);
        }
        catch (Exception ex)
        {
            return new Fetched<IReadOnlyList<FileChange>>.Failed(ex.Message);
        }
    }

    // "origin/main" -> "main"; "origin/feature/x" -> "feature/x". Remote names can't contain
    // slashes, so the local-branch name is everything after the first segment.
    private static string RemoteBranchShortName(Branch remote)
    {
        var name = remote.FriendlyName;
        var slash = name.IndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static void AddBadge(Dictionary<string, List<RefBadge>> map, string sha, RefBadge badge)
    {
        if (!map.TryGetValue(sha, out var list))
        {
            list = new List<RefBadge>();
            map[sha] = list;
        }
        list.Add(badge);
    }

    public Fetched<CommitDetails> LoadDetails(Repo repo, string sha)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<CommitDetails>.Failed("Not a git repository.");

            // One log call with NUL-separated fields. %B (raw message) is last so any
            // newlines inside it can't be confused with field boundaries. Split(_, 10)
            // caps the chunk count so a NUL inside the body (theoretical, not seen in
            // practice) lands in the body field rather than producing extra entries.
            const string fmt = "%H%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%P%x00%s%x00%B";
            var logOutput = RunGit(repo.Path, out var logErr, "log", "-1", $"--format={fmt}", sha);
            if (logOutput == null)
                return new Fetched<CommitDetails>.Failed(logErr ?? "Commit not found.");

            var parts = logOutput.Split('\0', 10);
            if (parts.Length < 10)
                return new Fetched<CommitDetails>.Failed("Unexpected git log output.");

            var resolvedSha = parts[0];
            var parentShas = parts[7].Length == 0
                ? Array.Empty<string>()
                : parts[7].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // --root makes the root commit emit additions (against the empty tree) instead
            // of erroring on the missing parent. -M enables rename detection. -z switches
            // to NUL-separated records (see ParseDiffTreeNameStatusZ).
            var diffOutput = RunGit(repo.Path, out _, "diff-tree", "-r", "-M", "--name-status",
                "--no-commit-id", "-z", "--root", resolvedSha);
            var files = ParseDiffTreeNameStatusZ(diffOutput ?? string.Empty);
            files.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            return new CommitDetails(
                RepoId: repo.Id,
                Sha: resolvedSha,
                AuthorName: parts[1],
                AuthorEmail: parts[2],
                AuthorWhen: ParseIsoDateOrDefault(parts[3]),
                CommitterName: parts[4],
                CommitterEmail: parts[5],
                CommitterWhen: ParseIsoDateOrDefault(parts[6]),
                Message: parts[9],
                MessageShort: parts[8],
                ParentShas: parentShas,
                Files: files);
        }
        catch (Exception ex)
        {
            return new Fetched<CommitDetails>.Failed(ex.Message);
        }
    }

    private static DateTimeOffset ParseIsoDateOrDefault(string s)
        => DateTimeOffset.TryParse(s, out var when) ? when : DateTimeOffset.MinValue;

    // Parses the NUL-separated output of `git diff-tree --name-status -z`. Each record is
    // "<status>\0<path>\0", except R/C records which carry a similarity score on the
    // status and a second path: "R100\0<old>\0<new>\0". Status letters map via the same
    // table as porcelain v2 (M/A/D/R/C/T).
    // Parses the NUL-separated output of `git diff -M --raw -z`. Each entry is a metadata record
    // ":<srcmode> <dstmode> <srcsha> <dstsha> <status>" followed by one path (two for R/C: old then
    // new). The after-side blob (<dstsha>) becomes the file's ContentId — the all-zero OID for a
    // deletion. Kept separate from ParseDiffTreeNameStatusZ so the shared name-status callers are
    // untouched.
    private static List<FileChange> ParseDiffRawZ(string output)
    {
        var files = new List<FileChange>();
        if (string.IsNullOrEmpty(output)) return files;
        var parts = output.Split('\0');
        var i = 0;
        while (i < parts.Length)
        {
            var meta = parts[i];
            if (string.IsNullOrEmpty(meta) || meta[0] != ':') { i++; continue; }

            // ":<srcmode> <dstmode> <srcsha> <dstsha> <status>" — dstsha is field 3, status field 4.
            var fields = meta.Split(' ');
            if (fields.Length < 5) { i++; continue; }
            var dstSha = fields[3];
            var status = fields[4];
            var letter = status[0];
            var kind = MapPorcelainCode(letter) ?? FileChangeStatus.Modified;

            if (letter is 'R' or 'C')
            {
                if (i + 2 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 2], parts[i + 1], kind) { ContentId = dstSha });
                i += 3;
            }
            else
            {
                if (i + 1 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 1], null, kind) { ContentId = dstSha });
                i += 2;
            }
        }
        return files;
    }

    private static List<FileChange> ParseDiffTreeNameStatusZ(string output)
    {
        var files = new List<FileChange>();
        if (string.IsNullOrEmpty(output)) return files;
        var parts = output.Split('\0');
        var i = 0;
        while (i < parts.Length)
        {
            var status = parts[i];
            if (string.IsNullOrEmpty(status)) { i++; continue; }
            var letter = status[0];
            var kind = MapPorcelainCode(letter) ?? FileChangeStatus.Modified;
            if (letter == 'R' || letter == 'C')
            {
                if (i + 2 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 2], parts[i + 1], kind));
                i += 3;
            }
            else
            {
                if (i + 1 >= parts.Length) break;
                files.Add(new FileChange(parts[i + 1], null, kind));
                i += 2;
            }
        }
        return files;
    }

    // Same AOT-marshalling story as GetDiff: libgit2 callbacks for branch enumeration trip
    // NativeAOT's reverse-pinvoke stubs, so remote branches don't show in published builds.
    // `git for-each-ref` returns the same data in one shot.
    private const char BranchFieldSep = '\x1F';

    // %(upstream:track) collapses two distinct cases to "": (a) no upstream configured at all,
    // (b) upstream is set and we are exactly in sync. So we also pull %(upstream) (the upstream
    // ref name) to tell them apart. %(HEAD) marks the checked-out branch with "*", folding in
    // what a separate `symbolic-ref HEAD` call used to report — one fewer git process per load.
    private static readonly string BranchRefFormat =
        $"%(HEAD){BranchFieldSep}%(objectname){BranchFieldSep}%(refname){BranchFieldSep}%(upstream:track,nobracket){BranchFieldSep}%(upstream)";

    public Fetched<BranchListing> GetBranches(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<BranchListing>.Failed("Not a git repository.");

            var remotesByName = SeedRemoteGroups(repo.Path, out var remErr);
            if (remotesByName == null)
                return new Fetched<BranchListing>.Failed(remErr ?? "git remote failed.");

            var branchesOut = RunGit(repo.Path, out var brErr,
                "for-each-ref", $"--format={BranchRefFormat}", "refs/heads", "refs/remotes");
            if (branchesOut == null)
                return new Fetched<BranchListing>.Failed(brErr ?? "git for-each-ref failed.");

            var locals = new List<BranchEntry>();
            foreach (var line in branchesOut.Split('\n'))
                ParseBranchRefLine(line, locals, remotesByName);

            SortLocalBranches(locals);
            var remoteGroups = BuildRemoteGroups(remotesByName);
            var stashes = LoadStashes(repo.Path);
            return new BranchListing(repo.Id, locals, remoteGroups, stashes);
        }
        catch (Exception ex)
        {
            return new Fetched<BranchListing>.Failed(ex.Message);
        }
    }

    // Seed with all configured remotes so groups still show even when a remote has no branches
    // yet (matches the prior LibGit2Sharp behavior). Returns null on a genuine git failure.
    private Dictionary<string, List<BranchEntry>>? SeedRemoteGroups(string repoPath, out string? error)
    {
        var remotesOut = RunGit(repoPath, out error, "remote");
        if (remotesOut == null) return null;
        var remotesByName = new Dictionary<string, List<BranchEntry>>(StringComparer.Ordinal);
        foreach (var rawLine in remotesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = rawLine.Trim();
            if (name.Length > 0) remotesByName[name] = new List<BranchEntry>();
        }
        return remotesByName;
    }

    private static void ParseBranchRefLine(
        string line, List<BranchEntry> locals, Dictionary<string, List<BranchEntry>> remotesByName)
    {
        if (line.Length == 0) return;
        var parts = line.Split(BranchFieldSep);
        if (parts.Length < 3) return;
        var refname = parts[2];
        if (refname.StartsWith("refs/heads/", StringComparison.Ordinal))
            locals.Add(ParseLocalBranch(parts, refname));
        else if (refname.StartsWith("refs/remotes/", StringComparison.Ordinal))
            AddRemoteBranch(refname, parts[1], remotesByName);
    }

    private static BranchEntry ParseLocalBranch(string[] parts, string refname)
    {
        // parts[0] is %(HEAD): "*" for the checked-out branch, a space otherwise.
        var name = refname["refs/heads/".Length..];
        var isHead = parts[0] == "*";
        var sha = parts[1];
        var track = parts.Length > 3 ? parts[3] : string.Empty;
        var upstream = parts.Length > 4 ? parts[4] : string.Empty;
        var (ahead, behind, upstreamState) = ParseUpstream(track, upstream);
        var (upstreamRemote, upstreamBranch) = SplitUpstreamRef(upstream, upstreamState);
        return new BranchEntry(name, sha, isHead,
            AheadBy: ahead, BehindBy: behind,
            UpstreamState: upstreamState,
            UpstreamRemote: upstreamRemote,
            UpstreamBranch: upstreamBranch);
    }

    private static (string? Remote, string? Branch) SplitUpstreamRef(string upstream, BranchUpstreamState state)
    {
        if (state != BranchUpstreamState.Tracked || !upstream.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return (null, null);
        var rest = upstream["refs/remotes/".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return (null, null);
        return (rest[..slash], rest[(slash + 1)..]);
    }

    private static void AddRemoteBranch(
        string refname, string sha, Dictionary<string, List<BranchEntry>> remotesByName)
    {
        var rest = refname["refs/remotes/".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return;
        var remoteName = rest[..slash];
        var display = rest[(slash + 1)..];
        // Skip the symbolic origin/HEAD ref; it just mirrors another branch.
        if (display == "HEAD") return;
        if (!remotesByName.TryGetValue(remoteName, out var list))
        {
            list = new List<BranchEntry>();
            remotesByName[remoteName] = list;
        }
        list.Add(new BranchEntry(display, sha, IsHead: false));
    }

    private static void SortLocalBranches(List<BranchEntry> locals) =>
        locals.Sort((a, b) =>
        {
            if (a.IsHead != b.IsHead) return a.IsHead ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

    private static List<RemoteGroup> BuildRemoteGroups(Dictionary<string, List<BranchEntry>> remotesByName)
    {
        var groups = new List<RemoteGroup>(remotesByName.Count);
        foreach (var kv in remotesByName.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            kv.Value.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            groups.Add(new RemoteGroup(kv.Key, kv.Value));
        }
        return groups;
    }

    // `git stash list` is the source of truth for stash@{N} indexing; refs/stash only
    // points at the most recent entry. Stash list runs through `git log`, so the format
    // codes are the log ones (%H, %s) — NOT the for-each-ref ones (%(objectname)) which
    // get printed literally here.
    private IReadOnlyList<StashEntry> LoadStashes(string repoPath)
    {
        const char Sep = '\x1F';
        var fmt = $"%H{Sep}%gs";
        var output = RunGit(repoPath, out _, "stash", "list", $"--format={fmt}");
        if (string.IsNullOrEmpty(output)) return Array.Empty<StashEntry>();

        var list = new List<StashEntry>();
        var idx = 0;
        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(Sep, 2);
            if (parts.Length < 2) continue;
            list.Add(new StashEntry(idx++, parts[0], StripStashPrefix(parts[1])));
        }
        return list;
    }

    // The reflog subject is "On <branch>: <msg>" (with -m) or
    // "WIP on <branch>: <sha> <commit-subject>" (without). Both are noise — the user
    // cares about the part after the first ": ".
    private static string StripStashPrefix(string reflogSubject)
    {
        var colon = reflogSubject.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return reflogSubject;
        return reflogSubject[(colon + 2)..];
    }

    // %(upstream:track,nobracket) returns "", "gone", "ahead N", "behind N", or
    // "ahead N, behind M". Empty is overloaded: it means EITHER no upstream configured
    // OR in sync with upstream — so we also key on %(upstream) (the upstream ref name,
    // empty when none is set) to disambiguate. "gone" = upstream was set but the remote
    // ref has since been deleted. The UI surfaces those as distinct states.
    private static (int? ahead, int? behind, BranchUpstreamState state) ParseUpstream(string track, string upstream)
    {
        if (string.IsNullOrEmpty(upstream)) return (null, null, BranchUpstreamState.NeverLinked);
        if (track == "gone") return (null, null, BranchUpstreamState.Gone);
        int? a = null, b = null;
        foreach (var part in track.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(p[6..], out var av)) a = av;
            else if (p.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(p[7..], out var bv)) b = bv;
        }
        return (a, b, BranchUpstreamState.Tracked);
    }

    // Shells out to `git status --porcelain=v2 -z` instead of libgit2's RetrieveStatus.
    // libgit2 doesn't register external filter drivers (git-lfs, custom clean/smudge), so
    // for LFS-tracked files it falls through to a stat-cache comparison that often reports
    // "unmodified" when the git CLI sees "modified" — e.g. right after a branch switch
    // where the smudged workdir was produced from a different pointer than HEAD now has.
    // Using the CLI keeps our view in sync with what git itself thinks the working tree
    // contains, the same reason GetBranches and GetDiff already shell out.
    public Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new Fetched<LocalChangesSnapshot>.Failed("Not a git repository.");

            var output = RunGitStatusPorcelain(repo.Path, out var error, out var detail);
            if (output == null)
                return new Fetched<LocalChangesSnapshot>.Failed(error ?? "git status failed.", detail);

            var staged = new List<FileChange>();
            var unstaged = new List<FileChange>();
            ParseStatusPorcelainV2(output, staged, unstaged);

            staged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            unstaged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            return new LocalChangesSnapshot(repo.Id, staged, unstaged);
        }
        catch (Exception ex)
        {
            return new Fetched<LocalChangesSnapshot>.Failed(ex.Message);
        }
    }

    // Porcelain v2 with -z is NUL-terminated. Most records are a single NUL-terminated line;
    // type-2 (rename/copy) records carry an additional NUL-terminated origPath right after, so
    // we walk byte-by-byte rather than splitting on NUL up front.
    private static void ParseStatusPorcelainV2(string output, List<FileChange> staged, List<FileChange> unstaged)
    {
        var idx = 0;
        while (idx < output.Length)
        {
            var end = output.IndexOf('\0', idx);
            if (end < 0) break;
            var record = output[idx..end];
            idx = end + 1;
            if (record.Length > 0)
                ParseStatusRecord(record, output, ref idx, staged, unstaged);
        }
    }

    private static void ParseStatusRecord(
        string record, string output, ref int idx, List<FileChange> staged, List<FileChange> unstaged)
    {
        switch (record[0])
        {
            case '?': // "? path" — untracked.
                var path = record.Length > 2 ? record[2..] : string.Empty;
                if (path.Length > 0)
                    unstaged.Add(new FileChange(path, null, FileChangeStatus.Added));
                break;
            case '!': // Ignored — not requested, but skip defensively if it ever appears.
                break;
            case 'u':
                ParseUnmergedRecord(record, unstaged);
                break;
            case '1':
                ParseOrdinaryRecord(record, staged, unstaged);
                break;
            case '2':
                ParseRenameRecord(record, output, ref idx, staged, unstaged);
                break;
        }
    }

    // "u XY sub m1 m2 m3 mW h1 h2 h3 path" — unmerged. Surface in unstaged only; the user has to
    // resolve and stage to clear it. Splitting into both panels would invite a half-staged
    // conflict resolution.
    private static void ParseUnmergedRecord(string record, List<FileChange> unstaged)
    {
        var parts = record.Split(' ', 11);
        if (parts.Length < 11) return;
        unstaged.Add(new FileChange(parts[10], null, FileChangeStatus.Conflicted));
    }

    private static void ParseOrdinaryRecord(string record, List<FileChange> staged, List<FileChange> unstaged)
    {
        // "1 XY sub mH mI mW hH hI path"
        var parts = record.Split(' ', 9);
        if (parts.Length < 9) return;
        var xy = parts[1];
        if (xy.Length < 2) return;
        AddIndexAndWorkdirEntries(staged, unstaged, xy[0], xy[1], parts[8], origPath: null);
    }

    // "2 XY sub mH mI mW hH hI Xscore path" followed by a NUL-terminated origPath, which this
    // consumes by advancing idx.
    private static void ParseRenameRecord(
        string record, string output, ref int idx, List<FileChange> staged, List<FileChange> unstaged)
    {
        var parts = record.Split(' ', 10);
        if (parts.Length < 10) return;
        var xy = parts[1];
        if (xy.Length < 2) return;
        var origEnd = output.IndexOf('\0', idx);
        if (origEnd < 0) return;
        var origPath = output[idx..origEnd];
        idx = origEnd + 1;
        AddIndexAndWorkdirEntries(staged, unstaged, xy[0], xy[1], parts[9], origPath);
    }

    private static void AddIndexAndWorkdirEntries(
        List<FileChange> staged, List<FileChange> unstaged,
        char x, char y, string path, string? origPath)
    {
        var indexStatus = MapPorcelainCode(x);
        if (indexStatus != null)
        {
            var oldPath = indexStatus == FileChangeStatus.Renamed || indexStatus == FileChangeStatus.Copied ? origPath : null;
            if (oldPath == path) oldPath = null;
            staged.Add(new FileChange(path, oldPath, indexStatus.Value));
        }

        var workStatus = MapPorcelainCode(y);
        if (workStatus != null)
        {
            var oldPath = workStatus == FileChangeStatus.Renamed || workStatus == FileChangeStatus.Copied ? origPath : null;
            if (oldPath == path) oldPath = null;
            unstaged.Add(new FileChange(path, oldPath, workStatus.Value));
        }
    }

    private static FileChangeStatus? MapPorcelainCode(char c) => c switch
    {
        'M' => FileChangeStatus.Modified,
        'A' => FileChangeStatus.Added,
        'D' => FileChangeStatus.Deleted,
        'R' => FileChangeStatus.Renamed,
        'C' => FileChangeStatus.Copied,
        'T' => FileChangeStatus.TypeChanged,
        'U' => FileChangeStatus.Conflicted,
        _ => null,
    };

    // Uses the direct git executable rather than the shell wrapper — status is read-only,
    // runs on every working-tree change, and doesn't need the interactive-shell env (no auth,
    // no PATH-dependent helpers). -z is required: it switches records to NUL termination and
    // disables the C-style quoting that wraps paths with spaces or unicode in the default
    // porcelain output.
    //
    // --ignore-submodules=dirty isolates the failure domain: without it, status runs a full
    // `git status --porcelain=2` *inside* each submodule to detect a dirty work tree, so a
    // transient submodule hiccup (a dropped --recurse-submodules fetch, an in-progress op)
    // fails the whole read with "failed in submodule X" — blanking the superproject's own file
    // list for changes that are perfectly readable. =dirty skips that inner recursion while
    // still reporting the submodule's committed pointer diff (the `SC`/`S` line) against HEAD
    // and the index, so both staged and unstaged pointer bumps render exactly as before; the
    // only thing dropped is a submodule whose internal work tree is dirty — which can't be
    // committed from the superproject anyway. Submodule pointer drift comes from the dedicated
    // ListSubmodules read (RepoSnapshotStore), which has its own failure domain.
    private string? RunGitStatusPorcelain(string workingDir, out string? error, out string? detail)
    {
        error = null;
        detail = null;
        var result = _runner.Run(
            workingDir,
            new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored=no", "--ignore-submodules=dirty" },
            GitProcessRunner.GitLaunch.Direct);
        if (result.Ok) return result.Stdout;
        // One-line headline for the inline placeholder; full block for the on-demand dialog.
        // The detail block keeps any trailing "fatal:"/"hint:" lines that FirstLineError drops.
        error = result.FirstLineError("git status");
        detail = result.BlockError("git status");
        return null;
    }

    // One `git status --porcelain=v2 --branch` read yielding the cheap per-repo signals the RepoBar
    // and toolbar need: branch / detached / upstream + ahead/behind (from the `# branch.*` headers)
    // and whether the working tree is dirty (any non-header record). Unlike the file-list read this
    // uses `--untracked-files=normal`, not `all`: the summary only needs a dirty *bool*, and an
    // untracked directory reports the same dirty=true either way — but `normal` stops at the first
    // entry per directory instead of recursing every untracked file, so the probe stays cheap on
    // repos with large untracked trees (the lag the ahead/behind number used to show after a sync).
    // Returns Unknown when the path isn't a repo (decorations should clear) and null when the
    // probe itself failed (a transient git failure — e.g. a crashed fsmonitor daemon — where the
    // caller keeps the last known status instead of zeroing it out).
    public GitStatusSummary? GetStatusSummary(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return GitStatusSummary.Unknown;
            var result = _runner.Run(
                repo.Path,
                new[] { "status", "--porcelain=v2", "--branch", "--untracked-files=normal", "--ignored=no", "--ignore-submodules=dirty" },
                GitProcessRunner.GitLaunch.Direct);
            return result.Ok ? ParseStatusSummary(result.Stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    // Porcelain v2 emits all `# branch.*` headers first, then one record per changed/untracked path.
    // So the first non-header line means "dirty" and every header is already parsed by then.
    private static GitStatusSummary ParseStatusSummary(string stdout)
    {
        string? branch = null;
        var detached = false;
        var hasUpstream = false;
        var ahead = 0;
        var behind = 0;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] != '#')
                return new GitStatusSummary(branch, detached, hasUpstream, ahead, behind, IsDirty: true);

            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var v = line["# branch.head ".Length..];
                if (v == "(detached)") detached = true;
                else branch = v;
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                hasUpstream = true;
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                ParseAheadBehind(line["# branch.ab ".Length..], out ahead, out behind);
            }
        }

        return new GitStatusSummary(branch, detached, hasUpstream, ahead, behind, IsDirty: false);
    }

    // "+<ahead> -<behind>", e.g. "+2 -3".
    private static void ParseAheadBehind(string s, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length < 2) continue;
            if (tok[0] == '+') int.TryParse(tok.AsSpan(1), out ahead);
            else if (tok[0] == '-') int.TryParse(tok.AsSpan(1), out behind);
        }
    }

    public GitOutcome Stage(Repo repo, IReadOnlyList<string> paths)
        => paths.Count == 0 ? GitOutcome.Ok : RunOperation(repo, () =>
        {
            var result = RunPathspecOp(repo.Path, new[] { "add" }, paths);
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError("git add"));
        });

    public GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths)
        => paths.Count == 0 ? GitOutcome.Ok : RunOperation(repo, () =>
        {
            var result = RunPathspecOp(repo.Path, new[] { "restore", "--staged" }, paths);
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError("git restore --staged"));
        });

    public GitOutcome TakeOurs(Repo repo, string path) => TakeSide(repo, path, ours: true);

    public GitOutcome TakeTheirs(Repo repo, string path) => TakeSide(repo, path, ours: false);

    // Resolves a conflict to one side: check out that side's blob, then stage it. Stage 2 is
    // "ours", stage 3 is "theirs". A delete/modify conflict is missing one of those stages —
    // choosing the side that deleted the file means removing it (`git rm`), not checking it out.
    private GitOutcome TakeSide(Repo repo, string path, bool ours)
        => RunOperation(repo, () =>
        {
            var stages = GetUnmergedStages(repo.Path, path);
            var wantStage = ours ? 2 : 3;
            // The chosen side deleted the file (its stage is absent but the path is unmerged):
            // resolve by removing it from index + working tree.
            if (stages.Count > 0 && !stages.Contains(wantStage))
                return Mutate(repo.Path, "rm", "-f", "--", path);

            var checkedOut = Mutate(repo.Path, "checkout", ours ? "--ours" : "--theirs", "--", path);
            if (checkedOut is GitOutcome.Failed) return checkedOut;

            return Mutate(repo.Path, "add", "--", path);
        });

    // Marks a manually-edited file resolved by staging it — `git add` is exactly how git
    // records a resolution. If the file is gone (the user resolved by deleting it), `git add`
    // fails, so fall back to `git rm` to clear the unmerged index entry.
    public GitOutcome MarkResolved(Repo repo, string path)
        => RunOperation(repo, () => File.Exists(Path.Combine(repo.Path, path))
            ? Mutate(repo.Path, "add", "--", path)
            : Mutate(repo.Path, "rm", "-f", "--", path));

    // Resolves a conflict by keeping both sides: writes ours' blob followed by theirs' blob
    // (a newline boundary inserted if ours doesn't end in one), then stages. Missing sides
    // (delete/modify) degrade to whichever side has content.
    public GitOutcome TakeBoth(Repo repo, string path)
        => RunOperation(repo, () =>
        {
            var ours = ShowStage(repo.Path, 2, path);
            var theirs = ShowStage(repo.Path, 3, path);
            if (ours == null && theirs == null)
                return new GitOutcome.Failed("Neither side has content to combine.");

            var combined = CombineSides(ours, theirs);
            var full = Path.Combine(repo.Path, path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, combined);

            return Mutate(repo.Path, "add", "--", path);
        });

    private static string CombineSides(string? ours, string? theirs)
    {
        if (string.IsNullOrEmpty(ours)) return theirs ?? string.Empty;
        if (string.IsNullOrEmpty(theirs)) return ours;
        return ours.EndsWith('\n') ? ours + theirs : ours + "\n" + theirs;
    }

    public ConflictContext? GetConflictContext(Repo repo, string path)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return null;

            var stages = GetUnmergedStages(repo.Path, path);
            if (stages.Count == 0) return null;   // not a conflict — caller shows the normal diff

            var hasBase = stages.Contains(1);
            var oursPresent = stages.Contains(2);
            var theirsPresent = stages.Contains(3);

            var operation = GetOperationState(repo);

            var oursSha = TrimOrNull(RunGit(repo.Path, out _, "rev-parse", "HEAD"));
            var oursMeta = GetCommitMeta(repo.Path, oursSha);
            var oursLabel = GetCurrentBranchLabel(repo.Path);

            var theirsSha = GetIncomingSha(repo.Path, operation);
            var theirsMeta = GetCommitMeta(repo.Path, theirsSha);
            var theirsLabel = GetRefLabelForSha(repo.Path, theirsSha) ?? DescribeIncoming(operation, theirsSha);

            return new ConflictContext(
                operation,
                new ConflictSideInfo(oursLabel, ShortSha(oursSha), oursMeta.Subject, oursMeta.When,
                    ChangeKind(hasBase, present: oursPresent)),
                new ConflictSideInfo(theirsLabel, ShortSha(theirsSha), theirsMeta.Subject, theirsMeta.When,
                    ChangeKind(hasBase, present: theirsPresent)),
                hasBase);
        }
        catch
        {
            return null;
        }
    }

    private static ConflictChangeKind ChangeKind(bool hasBase, bool present)
    {
        if (!present) return ConflictChangeKind.Deleted;
        return hasBase ? ConflictChangeKind.Modified : ConflictChangeKind.Added;
    }

    // The current branch name, or a short SHA when detached (e.g. mid-rebase).
    private string GetCurrentBranchLabel(string repoPath)
    {
        var name = RunGit(repoPath, out _, "symbolic-ref", "--short", "-q", "HEAD");
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        var sha = RunGit(repoPath, out _, "rev-parse", "--short", "HEAD");
        return string.IsNullOrWhiteSpace(sha) ? "HEAD" : sha.Trim();
    }

    // SHA of the incoming side, read from the operation's sentinel file in the gitdir.
    private string? GetIncomingSha(string repoPath, RepoOperationState op)
    {
        var gitDir = GetGitDir(repoPath);
        if (gitDir == null) return null;

        string? Read(params string[] rel)
        {
            var p = gitDir;
            foreach (var r in rel) p = Path.Combine(p, r);
            if (!File.Exists(p)) return null;
            var text = File.ReadAllText(p).Trim();
            // MERGE_HEAD can list several parents (octopus) — the first is enough to label.
            var nl = text.IndexOf('\n');
            return nl < 0 ? text : text[..nl];
        }

        return op switch
        {
            RepoOperationState.Merge => Read("MERGE_HEAD"),
            RepoOperationState.CherryPick => Read("CHERRY_PICK_HEAD"),
            RepoOperationState.Revert => Read("REVERT_HEAD"),
            RepoOperationState.Rebase => Read("rebase-merge", "stopped-sha") ?? Read("rebase-apply", "original-commit"),
            _ => Read("MERGE_HEAD") ?? Read("CHERRY_PICK_HEAD") ?? Read("REVERT_HEAD"),
        };
    }

    // A branch/remote ref name pointing exactly at the incoming commit, else null.
    private string? GetRefLabelForSha(string repoPath, string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return null;
        var pointed = RunGit(repoPath, out _, "for-each-ref", "--points-at", sha,
            "--format=%(refname:short)", "refs/heads", "refs/remotes");
        if (string.IsNullOrWhiteSpace(pointed)) return null;
        var first = pointed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return first.Length > 0 ? first[0].Trim() : null;
    }

    private static string DescribeIncoming(RepoOperationState op, string? sha)
    {
        var shortSha = ShortSha(sha);
        return op switch
        {
            RepoOperationState.CherryPick => string.IsNullOrEmpty(shortSha) ? "cherry-pick" : shortSha,
            RepoOperationState.Revert => string.IsNullOrEmpty(shortSha) ? "revert" : shortSha,
            RepoOperationState.Rebase => string.IsNullOrEmpty(shortSha) ? "rebase" : shortSha,
            _ => string.IsNullOrEmpty(shortSha) ? "incoming" : shortSha,
        };
    }

    // Commit subject + committer date for a SHA. Uses a unit-separator between fields so the
    // subject can contain anything. Empty/min on any failure.
    private (string Subject, DateTimeOffset When) GetCommitMeta(string repoPath, string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return (string.Empty, DateTimeOffset.MinValue);
        var output = RunGit(repoPath, out _, "show", "-s", "--format=%s%x1f%cI", sha);
        if (string.IsNullOrWhiteSpace(output)) return (string.Empty, DateTimeOffset.MinValue);
        var parts = output.Trim().Split('\x1f');
        var subject = parts.Length > 0 ? parts[0] : string.Empty;
        var when = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var d) ? d : DateTimeOffset.MinValue;
        return (subject, when);
    }

    public RepoOperation? GetOperation(Repo repo)
    {
        var state = GetOperationState(repo);
        if (state == RepoOperationState.None) return null;
        var path = repo.Path;
        int conflicts;
        try { conflicts = CountUnmergedPaths(path); }
        catch { conflicts = 0; }

        switch (state)
        {
            case RepoOperationState.Rebase:
            {
                var (step, total) = ReadRebaseProgress(path);
                return new RebaseOperation(ReadRebaseHeadName(path), ReadRebaseOnto(path), step, total, SubjectFor(path, state), conflicts);
            }
            case RepoOperationState.ApplyMailbox:
            {
                var (step, total) = ReadRebaseProgress(path);
                return new ApplyMailboxOperation(step, total, SubjectFor(path, state), conflicts);
            }
            case RepoOperationState.CherryPick:
                return new CherryPickOperation(SubjectFor(path, state), conflicts);
            case RepoOperationState.Revert:
                return new RevertOperation(SubjectFor(path, state), conflicts);
            case RepoOperationState.Merge:
                return new MergeOperation(IncomingLabelFor(path, state), conflicts);
            case RepoOperationState.Bisect:
                return new BisectOperation();
            case RepoOperationState.UnmergedPaths:
                return new UnmergedPathsOperation(conflicts);
            default:
                return null;
        }
    }

    private string? SubjectFor(string repoPath, RepoOperationState state)
    {
        try
        {
            var sha = GetIncomingSha(repoPath, state);
            if (string.IsNullOrEmpty(sha)) return null;
            var (subject, _) = GetCommitMeta(repoPath, sha);
            return string.IsNullOrWhiteSpace(subject) ? null : subject;
        }
        catch { return null; }
    }

    private string? IncomingLabelFor(string repoPath, RepoOperationState state)
    {
        try
        {
            var sha = GetIncomingSha(repoPath, state);
            if (string.IsNullOrEmpty(sha)) return null;
            return GetRefLabelForSha(repoPath, sha) ?? ShortSha(sha);
        }
        catch { return null; }
    }

    private (int Step, int Total) ReadRebaseProgress(string repoPath)
    {
        var gitDir = GetGitDir(repoPath);
        if (gitDir == null) return (0, 0);
        var merge = Path.Combine(gitDir, "rebase-merge");
        if (Directory.Exists(merge))
            return (ReadCount(Path.Combine(merge, "msgnum")), ReadCount(Path.Combine(merge, "end")));
        var apply = Path.Combine(gitDir, "rebase-apply");
        if (Directory.Exists(apply))
            return (ReadCount(Path.Combine(apply, "next")), ReadCount(Path.Combine(apply, "last")));
        return (0, 0);
    }

    private string? ReadRebaseOnto(string repoPath)
    {
        var gitDir = GetGitDir(repoPath);
        if (gitDir == null) return null;
        var sha = ReadSentinel(Path.Combine(gitDir, "rebase-merge", "onto"))
                  ?? ReadSentinel(Path.Combine(gitDir, "rebase-apply", "onto"));
        if (string.IsNullOrEmpty(sha)) return null;
        return GetRefLabelForSha(repoPath, sha) ?? ShortSha(sha);
    }

    private string? ReadRebaseHeadName(string repoPath)
    {
        var gitDir = GetGitDir(repoPath);
        if (gitDir == null) return null;
        var name = ReadSentinel(Path.Combine(gitDir, "rebase-merge", "head-name"))
                   ?? ReadSentinel(Path.Combine(gitDir, "rebase-apply", "head-name"));
        if (string.IsNullOrEmpty(name)) return null;
        const string prefix = "refs/heads/";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    private int CountUnmergedPaths(string repoPath)
    {
        var output = RunGit(repoPath, out _, "ls-files", "--unmerged");
        if (string.IsNullOrWhiteSpace(output)) return 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab >= 0 && tab + 1 < line.Length) paths.Add(line[(tab + 1)..]);
        }
        return paths.Count;
    }

    private static int ReadCount(string path)
    {
        try { return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var n) ? n : 0; }
        catch { return 0; }
    }

    private static string? ReadSentinel(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static string? TrimOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha[..7] : sha);

    // Blob text for one merge stage (1=base, 2=ours, 3=theirs). Null when the stage is absent
    // — `git show :n:path` exits non-zero, which we treat as "this side doesn't exist".
    private string? ShowStage(string repoPath, int stage, string path)
    {
        var result = _runner.Run(repoPath, new[] { "show", $":{stage}:{path}" }, GitProcessRunner.GitLaunch.Direct);
        return result.Ok ? result.Stdout : null;
    }

    // Which conflict stages (1/2/3) exist for an unmerged path. `git ls-files -u` lists one
    // line per present stage: "<mode> <sha> <stage>\t<path>". Empty when the path isn't unmerged.
    private HashSet<int> GetUnmergedStages(string repoPath, string path)
    {
        var stages = new HashSet<int>();
        var output = RunGit(repoPath, out _, "ls-files", "-u", "--", path);
        if (string.IsNullOrEmpty(output)) return stages;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length >= 3 && int.TryParse(meta[2], out var stage))
                stages.Add(stage);
        }
        return stages;
    }

    public GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse)
        => string.IsNullOrEmpty(patch) ? GitOutcome.Ok : RunOperation(repo, () =>
        {
            var args = new List<string> { "apply", "--whitespace=nowarn" };
            if (cached) args.Add("--cached");
            if (reverse) args.Add("--reverse");
            args.Add("-");
            var result = _runner.Run(repo.Path, args, GitProcessRunner.GitLaunch.Direct, patch);
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError("git apply"));
        });

    public GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths)
        => paths.Count == 0 ? GitOutcome.Ok : RunOperation(repo, () =>
        {
            // No HEAD (unborn branch) → nothing to reset to. Root commit (HEAD with no
            // parent) → `git reset` has nothing to copy from, so drop the entries from
            // the index without touching the workdir. Otherwise let `git reset HEAD^`
            // copy parent blobs back into the index (or remove entries the parent didn't
            // have). The working tree is untouched in all paths.
            if (RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD") == null)
                return GitOutcome.Ok;
            var hasParent = RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD^") != null;
            var preArgs = hasParent
                ? new[] { "reset", "HEAD^" }
                : new[] { "rm", "--cached", "--force" };
            var result = RunPathspecOp(repo.Path, preArgs, paths);
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError($"git {string.Join(' ', preArgs)}"));
        });

    // Throws away unstaged workdir changes for the given paths. Tracked files are restored
    // from the index via `git checkout -- <paths>` (the user's staged hunks are preserved);
    // untracked files (not in the index) are deleted from disk.
    public GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths)
        => paths.Count == 0 ? GitOutcome.Ok : RunOperation(repo, () =>
        {
            // `git ls-files -z -- <paths>` prints only the tracked subset, NUL-separated.
            // Anything not in that subset exists only on disk and gets deleted directly;
            // tracked entries fall through to the `git checkout --` restore below.
            // ls-files has no --pathspec-from-file, so the selection is always chunked to
            // stay under the Windows command-line cap.
            var tracked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in ChunkPathsForCommandLine(paths))
            {
                var lsArgs = new List<string>(batch.Count + 3) { "ls-files", "-z", "--" };
                lsArgs.AddRange(batch);
                var lsOutput = RunGit(repo.Path, out var lsErr, lsArgs.ToArray());
                if (lsOutput == null) return new GitOutcome.Failed(lsErr ?? "git ls-files failed.");
                foreach (var t in lsOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    tracked.Add(t);
            }

            var trackedPaths = new List<string>();
            foreach (var p in paths)
            {
                if (tracked.Contains(p))
                {
                    trackedPaths.Add(p);
                    continue;
                }
                var fullPath = Path.Combine(repo.Path, p);
                try
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
                }
                catch (Exception ex)
                {
                    return new GitOutcome.Failed(ex.Message);
                }
            }

            if (trackedPaths.Count > 0)
            {
                var result = RunPathspecOp(repo.Path, new[] { "checkout" }, trackedPaths);
                if (!result.Ok) return new GitOutcome.Failed(result.FirstLineError("git checkout"));
            }
            return GitOutcome.Ok;
        });

    public GitOutcome Commit(Repo repo, string message, bool amend)
        => RunOperation(repo, () =>
        {
            var args = new List<string> { "commit", "-m", message };
            if (amend) args.Add("--amend");

            // -m supplies the message, but a configured core.editor would still fire for
            // merge/rebase/squash flows that prompt to confirm the commit message.
            var result = _runner.Run(repo.Path, args,
                configure: static psi => psi.EnvironmentVariables["GIT_EDITOR"] = "true");
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError("git commit"));
        });

    public HeadCommitMessage? GetHeadCommitMessage(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return null;
            // %s is git's subject (first line), %b is the body (everything after the
            // blank line after the subject). NUL separates them so a body containing
            // any newline pattern can't be confused for the boundary. Fails if there
            // are no commits yet (unborn branch) — RunGit returns null and we propagate.
            var output = RunGit(repo.Path, out _, "log", "-1", "--format=%s%x00%b", "HEAD");
            if (output == null) return null;
            var nul = output.IndexOf('\0');
            if (nul < 0) return new HeadCommitMessage(output.Trim(), string.Empty);
            var title = output[..nul].Trim();
            var body = output[(nul + 1)..].TrimEnd();
            return new HeadCommitMessage(title, body);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<FileChange> GetAmendStagedFiles(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return Array.Empty<FileChange>();
            if (RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD") == null)
                return Array.Empty<FileChange>();

            List<FileChange> files;
            if (RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD^") != null)
            {
                // Index vs HEAD's parent — the contents the amended commit would record.
                // Same NUL `--name-status` format as diff-tree, so the parser is shared.
                var output = RunGit(repo.Path, out _, "diff", "--cached", "-M", "--name-status", "-z", "HEAD^");
                if (output == null) return Array.Empty<FileChange>();
                files = ParseDiffTreeNameStatusZ(output);
            }
            else
            {
                // Amending the root commit: no parent to diff against, so every index
                // entry is an add.
                var output = RunGit(repo.Path, out _, "ls-files", "--cached", "-z");
                if (output == null) return Array.Empty<FileChange>();
                files = new List<FileChange>();
                foreach (var path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    files.Add(new FileChange(path, null, FileChangeStatus.Added));
            }
            files.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return files;
        }
        catch
        {
            return Array.Empty<FileChange>();
        }
    }

    // Snapshot of HEAD's branch + upstream tracking state. We avoid `git status --branch`
    // here because porcelain v2 scans the entire working tree for file changes too — for
    // a HEAD-only probe (called on every refresh, every push/pull preflight) the cost
    // shows on large repos. Three targeted commands stay fast.
    private readonly record struct HeadInfo(
        string? CurrentBranchName,
        bool IsDetached,
        bool HasUpstream,
        int Ahead,
        int Behind);

    private HeadInfo GetHeadInfo(string repoPath)
    {
        // symbolic-ref returns nonzero on detached HEAD; @{u} returns nonzero with no upstream.
        var branchOutput = RunGit(repoPath, out _, "symbolic-ref", "-q", "--short", "HEAD");
        if (branchOutput == null)
            return new HeadInfo(null, IsDetached: true, HasUpstream: false, Ahead: 0, Behind: 0);
        var branchName = branchOutput.Trim();
        if (branchName.Length == 0)
            return new HeadInfo(null, IsDetached: true, HasUpstream: false, Ahead: 0, Behind: 0);

        var upstreamOutput = RunGit(repoPath, out _, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (string.IsNullOrWhiteSpace(upstreamOutput))
            return new HeadInfo(branchName, IsDetached: false, HasUpstream: false, Ahead: 0, Behind: 0);

        int ahead = 0, behind = 0;
        var counts = RunGit(repoPath, out _, "rev-list", "--left-right", "--count", "HEAD...@{u}");
        if (counts != null)
        {
            var parts = counts.Trim().Split('\t');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out ahead);
                int.TryParse(parts[1], out behind);
            }
        }
        return new HeadInfo(branchName, IsDetached: false, HasUpstream: true, Ahead: ahead, Behind: behind);
    }

    // `git rev-parse --git-dir` returns the per-worktree gitdir (the worktree's own
    // .git/worktrees/<name>/ for secondary worktrees, the main .git/ otherwise), which
    // is the same thing libgit2's Repository.Info.Path gave us. Returns null on failure
    // so callers can decide whether that's a problem or just means "no in-progress op".
    private string? GetGitDir(string repoPath)
    {
        var output = RunGit(repoPath, out _, "rev-parse", "--git-dir");
        if (string.IsNullOrWhiteSpace(output)) return null;
        var dir = output.Trim();
        if (!Path.IsPathRooted(dir)) dir = Path.GetFullPath(Path.Combine(repoPath, dir));
        return dir;
    }

    // `git ls-files --unmerged` prints stage-2/stage-3 entries — one line per unmerged
    // path. Empty output means the index is fully merged.
    private bool HasUnmergedPaths(string repoPath)
    {
        var output = RunGit(repoPath, out _, "ls-files", "--unmerged");
        return !string.IsNullOrWhiteSpace(output);
    }

    public bool HasUnmergedPaths(Repo repo)
    {
        try { return IsGitRepo(repo.Path) && HasUnmergedPaths(repo.Path); }
        catch { return false; }
    }

    public string? GetMergeMessage(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return null;
            var gitDir = GetGitDir(repo.Path);
            if (gitDir == null) return null;
            // MERGE_HEAD is the merge sentinel; gate on it so cherry-pick/revert (which use
            // their own heads) don't trip this.
            if (!File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return null;
            var msgPath = Path.Combine(gitDir, "MERGE_MSG");
            return File.Exists(msgPath) ? File.ReadAllText(msgPath) : "Merge";
        }
        catch { return null; }
    }

    public PushStatus GetPushStatus(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new PushStatus(null, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false);

            var info = GetHeadInfo(repo.Path);
            return new PushStatus(
                CurrentBranchName: info.CurrentBranchName,
                HasUpstream: info.HasUpstream,
                Ahead: info.Ahead,
                Behind: info.Behind,
                IsDetached: info.IsDetached);
        }
        catch
        {
            return new PushStatus(null, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false);
        }
    }

    public DetachedHeadReport GetDetachedHeadReport(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return DetachedHeadReport.None;
            if (GetOperationState(repo) != RepoOperationState.None) return DetachedHeadReport.None;
            if (!GetHeadInfo(repo.Path).IsDetached) return DetachedHeadReport.None;

            var path = repo.Path;

            // Submodules are routinely parked on a detached HEAD by the superproject, usually
            // right on a branch tip. Offer to attach onto that branch rather than nagging.
            if (repo.IsSubmodule)
            {
                // A local branch already at HEAD → plain checkout.
                var localAtHead = FirstLine(RunGit(path, out _, "for-each-ref", "--points-at=HEAD",
                    "--format=%(refname:short)", "refs/heads"));
                if (localAtHead != null)
                    return new DetachedHeadReport(DetachedHeadKind.OnBranchTip, localAtHead);

                // A remote branch at HEAD whose local counterpart is absent (create it) or merely
                // behind (fast-forward it). A diverged local counterpart isn't safely attachable,
                // so skip that candidate.
                foreach (var remoteRef in RemoteBranchesAtHead(path))
                {
                    var slash = remoteRef.IndexOf('/');
                    if (slash <= 0 || slash == remoteRef.Length - 1) continue;
                    var shortName = remoteRef[(slash + 1)..];
                    var localRef = $"refs/heads/{shortName}";
                    if (!RefExists(path, localRef) || IsAncestor(path, localRef, "HEAD"))
                        return new DetachedHeadReport(DetachedHeadKind.OnBranchTip, shortName);
                }

                // Not on a switchable tip. Only warn when the HEAD commit is contained in no
                // branch at all — genuinely stranded commits. A submodule merely pinned to an
                // older commit (an ancestor of a branch) is reachable, so stay silent.
                var containing = RunGit(path, out _, "for-each-ref", "--contains=HEAD",
                    "--format=%(refname)", "refs/heads", "refs/remotes");
                return string.IsNullOrWhiteSpace(containing)
                    ? new DetachedHeadReport(DetachedHeadKind.AtRisk)
                    : DetachedHeadReport.None;
            }

            // Top-level repo: if any branch/tag already points at the HEAD commit it's reachable
            // by name — nothing to lose. Only when no named ref lands on HEAD are these commits
            // reachable solely from HEAD and orphaned by a checkout.
            var pointed = RunGit(path, out _, "for-each-ref", "--points-at=HEAD",
                "--format=%(refname)", "refs/heads", "refs/remotes", "refs/tags");
            return string.IsNullOrWhiteSpace(pointed)
                ? new DetachedHeadReport(DetachedHeadKind.AtRisk)
                : DetachedHeadReport.None;
        }
        catch
        {
            return DetachedHeadReport.None;
        }
    }

    // Attach a detached HEAD onto `branch`: checks it out when its tip is already at HEAD,
    // fast-forwards it onto HEAD when it's behind, or creates it tracking a remote branch that
    // sits at HEAD. Refuses when a local branch of that name has diverged from HEAD (would drop
    // commits). Intended for the cases GetDetachedHeadReport flags as OnBranchTip.
    public GitOutcome AttachDetachedHead(Repo repo, string branch)
        => RunOperation(repo, () =>
        {
            var path = repo.Path;
            var localRef = $"refs/heads/{branch}";
            if (RefExists(path, localRef))
            {
                if (!IsAncestor(path, localRef, "HEAD"))
                    return new GitOutcome.Failed($"Local branch '{branch}' has diverged from HEAD.");
                // -B resets the branch to HEAD (a no-op when already there, a fast-forward when
                // behind) and checks it out in one step.
                return RunGitCheckout(path, new[] { "checkout", "-B", branch, "HEAD" });
            }

            var remote = RemoteBranchesAtHead(path)
                .Select(r => r.IndexOf('/') is var i && i > 0 && r[(i + 1)..] == branch ? r[..i] : null)
                .FirstOrDefault(r => r != null);
            if (remote == null)
                return new GitOutcome.Failed($"No branch '{branch}' at HEAD to switch to.");
            return RunGitCheckout(path, new[]
                { "checkout", "-b", branch, "--track", $"{remote}/{branch}" });
        });

    private IEnumerable<string> RemoteBranchesAtHead(string path)
    {
        var raw = RunGit(path, out _, "for-each-ref", "--points-at=HEAD",
            "--format=%(refname:short)", "refs/remotes");
        foreach (var line in raw.Split('\n'))
        {
            var name = line.Trim();
            // Skip the symbolic "origin/HEAD" pointer — it's not a branch to attach to.
            if (name.Length == 0 || name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            yield return name;
        }
    }

    private static string? FirstLine(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    private bool RefExists(string path, string fullRef)
        => !string.IsNullOrWhiteSpace(RunGit(path, out _, "rev-parse", "--verify", "--quiet", fullRef));

    private bool IsAncestor(string path, string maybeAncestor, string descendant)
        => _runner.Run(path, new[] { "merge-base", "--is-ancestor", maybeAncestor, descendant }).Ok;

    // After the superproject moves submodule working trees, `git submodule update` parks each
    // submodule on a detached HEAD even when the recorded commit is a branch tip. Land those back
    // on their branch so the user isn't left detached (and can't accidentally strand commits).
    // Best-effort: only touches submodules cleanly attachable onto a tip (see AttachDetachedHead)
    // and never fails the caller. Runs inside the caller's superproject lock; each attach takes
    // the submodule's own (distinct) lock.
    private void ReattachSubmodulesOnBranchTip(Repo primary)
    {
        try
        {
            foreach (var sub in ListSubmodules(primary))
            {
                if (sub.Status == SubmoduleStatus.NotInitialized) continue;
                var subRepo = new Repo(Guid.NewGuid(), sub.AbsolutePath, sub.Path) { Kind = RepoKind.Submodule };
                var report = GetDetachedHeadReport(subRepo);
                if (report.Kind == DetachedHeadKind.OnBranchTip && report.Branch is { } branch)
                    AttachDetachedHead(subRepo, branch);
            }
        }
        catch
        {
            // Reattachment is a convenience; a failure here must not fail the pull/update.
        }
    }

    // Shells out to the `git` CLI so we inherit the user's credential helpers
    // (ssh-agent, osxkeychain, GitHub CLI, …) — libgit2's macOS SSH path is too brittle.
    //
    // force=true uses --force-with-lease: refuses if the remote moved since our last fetch,
    // so a teammate's concurrent push isn't silently clobbered. Caller is expected to have
    // confirmed with the user before passing force=true.
    public GitOutcome Push(Repo repo, bool force = false)
        => RunOperation(repo, () =>
        {
            // Pre-flight: refuse to push from detached HEAD or a branch with no upstream,
            // because the resulting `git push` error is less actionable than these messages.
            var info = GetHeadInfo(repo.Path);
            if (info.IsDetached)
                return new GitOutcome.Failed("HEAD is detached. Check out a branch first.");
            if (!info.HasUpstream)
            {
                var name = info.CurrentBranchName ?? "(unknown)";
                return new GitOutcome.Failed(
                    $"Branch '{name}' has no upstream. Set one with: git push -u <remote> {name}");
            }

            var args = new List<string> { "push" };
            if (force) args.Add("--force-with-lease");
            return ToOutcome(_runner.Run(repo.Path, args), "git push");
        });

    public GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream)
        => RunOperation(repo, () =>
        {
            if (string.IsNullOrWhiteSpace(localBranch))
                return new GitOutcome.Failed("Local branch is required.");
            if (string.IsNullOrWhiteSpace(remoteName))
                return new GitOutcome.Failed("Remote is required.");
            if (string.IsNullOrWhiteSpace(remoteBranchName))
                return new GitOutcome.Failed("Remote branch name is required.");

            var args = new List<string> { "push" };
            if (setUpstream) args.Add("--set-upstream");
            args.Add(remoteName);
            args.Add($"{localBranch}:{remoteBranchName}");
            return ToOutcome(_runner.Run(repo.Path, args), "git push");
        });

    public IReadOnlyList<string> GetRemoteNames(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return Array.Empty<string>();
            return ReadRemoteNames(repo.Path, inject: true, out _);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public string? GetRemoteUrl(Repo repo, string remoteName)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return null;
            return ReadRemoteUrl(repo.Path, remoteName, inject: true, out _);
        }
        catch
        {
            return null;
        }
    }

    // Shared core for both the UI-facing remote reads (inject:true, errors swallowed) and the
    // resolver's raw reads (inject:false, errors surfaced). `error` reports a git failure distinct
    // from a successful read that found no remotes.
    private IReadOnlyList<string> ReadRemoteNames(string repoPath, bool inject, out string? error)
    {
        var output = RunGitInternal(repoPath, allowExitCode1: false, out error, new[] { "remote" }, inject: inject);
        if (error != null || string.IsNullOrEmpty(output)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim();
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    private string? ReadRemoteUrl(string repoPath, string remoteName, bool inject, out string? error)
    {
        var output = RunGitInternal(repoPath, allowExitCode1: false, out error, new[] { "remote", "get-url", remoteName }, inject: inject);
        if (error != null || output == null) return null;
        var url = output.Trim();
        return url.Length == 0 ? null : url;
    }

    public GitOutcome EditRemote(Repo repo, string oldName, string newName, string url)
        => RunOperation(repo, () =>
        {
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                var renamed = Mutate(repo.Path, "remote", "rename", oldName, newName);
                if (renamed is GitOutcome.Failed) return renamed;
            }
            return Mutate(repo.Path, "remote", "set-url", newName, url);
        });

    public GitOutcome AddRemote(Repo repo, string name, string url)
        => RunOperation(repo, () => Mutate(repo.Path, "remote", "add", name, url));

    public PullOutcome Pull(Repo repo, PullStrategy? strategy = null)
        => RunLocked<PullOutcome>(repo, () =>
        {
            var info = GetHeadInfo(repo.Path);
            if (info.IsDetached)
                return new PullOutcome.Failed("HEAD is detached. Check out a branch first.");
            if (!info.HasUpstream)
            {
                var name = info.CurrentBranchName ?? "(unknown)";
                return new PullOutcome.Failed(
                    $"Branch '{name}' has no upstream. Set one with: git branch --set-upstream-to=<remote>/<branch>");
            }

            // --recurse-submodules fetches the submodule commits referenced by the new
            // superproject tree AND checks each submodule's working tree out to the SHA the
            // parent now records — so the user doesn't end up with the gitlink pointer moved
            // but the submodule still sitting on its old commit (which shows up as "modified").
            var args = new List<string> { "pull" };
            // With no strategy git refuses a diverged branch ("Need to specify how to reconcile");
            // an explicit flag is what the reconcile dialog passes on the rerun. Once a strategy
            // is supplied git won't emit the hint again, so Diverged self-clears on the rerun.
            switch (strategy)
            {
                case PullStrategy.Merge: args.Add("--no-rebase"); break;
                case PullStrategy.Rebase: args.Add("--rebase"); break;
                case PullStrategy.FastForwardOnly: args.Add("--ff-only"); break;
            }
            args.Add("--recurse-submodules");

            var result = _runner.Run(repo.Path, args);
            if (result.Ok)
            {
                ReattachSubmodulesOnBranchTip(repo);
                return PullOutcome.Ok;
            }

            if (strategy is null && result.PreferredStream.Contains("divergent branches", StringComparison.OrdinalIgnoreCase))
                return new PullOutcome.Diverged();
            return new PullOutcome.Failed(result.BlockError("git pull"));
        }, static m => new PullOutcome.Failed(m));

    // --recurse-submodules downloads the commits each submodule is pinned to so they're
    // present locally; it does NOT touch submodule working trees (that's pull's job).
    public GitOutcome Fetch(Repo repo)
        => RunSimple(repo, "git fetch", "fetch", "--all", "--prune", "--recurse-submodules");

    // Clone has no existing Repo to lock or validate — it creates one. We run from the target's
    // parent dir (created if missing) with an absolute destination so git places the working tree
    // exactly where the dialog asked. Streaming surfaces "Receiving objects" progress and lets us
    // augment auth failures the same way fetch/push do.
    public CloneOutcome Clone(string url, string targetPath, Action<string>? onLine = null)
    {
        try
        {
            var trimmedUrl = url?.Trim() ?? string.Empty;
            if (trimmedUrl.Length == 0)
                return new CloneOutcome.Failed("Repository URL is required.");
            if (string.IsNullOrWhiteSpace(targetPath))
                return new CloneOutcome.Failed("Destination path is required.");

            string fullTarget;
            try { fullTarget = Path.GetFullPath(targetPath); }
            catch (Exception ex) { return new CloneOutcome.Failed($"Invalid destination path: {ex.Message}"); }

            if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
                return new CloneOutcome.Failed($"Destination already exists and is not empty:\n{fullTarget}");

            var parent = Path.GetDirectoryName(fullTarget);
            if (string.IsNullOrEmpty(parent))
                return new CloneOutcome.Failed("Destination path has no parent directory.");

            try { Directory.CreateDirectory(parent); }
            catch (Exception ex) { return new CloneOutcome.Failed($"Could not create destination folder: {ex.Message}"); }

            var args = new List<string> { "clone", "--progress", trimmedUrl, fullTarget };
            var (exitCode, captureText, started) = _runner.RunStreaming(parent, args, onLine);

            if (!started) return new CloneOutcome.Failed("Failed to start git.");

            if (exitCode == 0)
                return new CloneOutcome.Cloned(fullTarget);

            var msg = GitProcessRunner.FirstMeaningfulLine(captureText);
            if (string.IsNullOrEmpty(msg)) msg = $"git clone exited with code {exitCode}.";
            return new CloneOutcome.Failed(GitProcessRunner.AugmentCredentialError(msg, captureText));
        }
        catch (Exception ex)
        {
            return new CloneOutcome.Failed(ex.Message);
        }
    }

    public GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null)
        => RunOperation(repo, () =>
        {
            var refspec = $"{remoteBranch}:{localBranch}";
            var args = new List<string> { "fetch", "--progress", remoteName, refspec };
            var (exitCode, captureText, started) = _runner.RunStreaming(repo.Path, args, onLine);

            if (!started) return new GitOutcome.Failed("Failed to start git.");
            if (exitCode == 0) return GitOutcome.Ok;

            var msg = GitProcessRunner.FirstMeaningfulLine(captureText);
            if (string.IsNullOrEmpty(msg)) msg = $"git fetch exited with code {exitCode}.";
            return new GitOutcome.Failed(GitProcessRunner.AugmentCredentialError(msg, captureText));
        });

    // Shells out so post-checkout hooks, LFS, and sparse-checkout filters all run; also
    // surfaces the same error wording the user would see in Terminal.
    public GitOutcome CheckoutLocalBranch(Repo repo, string branchName)
        => RunOperation(repo, () => RunGitCheckout(repo.Path, new[] { "checkout", branchName }));

    public GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode)
        => RunOperation(repo, () =>
        {
            var flag = mode switch
            {
                ResetMode.Soft => "--soft",
                ResetMode.Mixed => "--mixed",
                ResetMode.Hard => "--hard",
                _ => "--mixed",
            };
            var result = _runner.Run(repo.Path, new[] { "reset", flag, commitSha });
            return result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.FirstLineError("git reset"));
        });

    public GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track)
        => RunOperation(repo, () => RunGitCheckout(repo.Path, new List<string>
        {
            "checkout", "-b", localName,
            track ? "--track" : "--no-track",
            $"{remoteName}/{remoteBranchName}",
        }));

    // Shells out so post-checkout hooks run when `checkout` is true, and the error wording
    // matches the user's terminal experience (e.g. "fatal: A branch named 'x' already exists.").
    public GitOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout)
        => RunOperation(repo, () =>
        {
            var args = checkout
                ? new[] { "checkout", "-b", name, startPoint }
                : new[] { "branch", name, startPoint };
            var result = _runner.Run(repo.Path, args);
            return result.Ok
                ? GitOutcome.Ok
                : new GitOutcome.Failed(result.FirstLineError($"git {(checkout ? "checkout" : "branch")}"));
        });

    // Force-moves an existing branch to point at commitSha. With checkout=true uses
    // `git checkout -B <branch> <sha>` (reset the ref AND switch to it in one step) — the path
    // used to bring detached-HEAD commits back onto a branch and land on it. The branch must
    // not be the currently checked-out one; callers only invoke this while detached, so it
    // never is. Force can orphan the branch's prior unique commits — callers guard via
    // IsAncestor and confirm before calling when it isn't a fast-forward.
    public GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout)
        => RunOperation(repo, () =>
        {
            var args = checkout
                ? new[] { "checkout", "-B", branchName, commitSha }
                : new[] { "branch", "-f", branchName, commitSha };
            var result = _runner.Run(repo.Path, args);
            return result.Ok
                ? GitOutcome.Ok
                : new GitOutcome.Failed(result.FirstLineError($"git {(checkout ? "checkout" : "branch")}"));
        });

    // True when maybeAncestor (a ref or SHA) is an ancestor of descendant — i.e. moving
    // maybeAncestor forward to descendant is a fast-forward that orphans nothing. Exit 0 =
    // ancestor, 1 = not, other = error (treated as "not", so callers confirm before forcing).
    public bool IsAncestor(Repo repo, string maybeAncestor, string descendant)
    {
        if (!IsGitRepo(repo.Path)) return false;
        var result = _runner.Run(
            repo.Path,
            new[] { "merge-base", "--is-ancestor", maybeAncestor, descendant },
            GitProcessRunner.GitLaunch.Direct);
        return result.ExitCode == 0;
    }

    // The merge-base (common-ancestor) SHA of two refs/SHAs via `git merge-base a b`, trimmed.
    // Null when git fails (bad ref, exit 128) or the histories are unrelated (exit 1) — RunGit
    // returns null on any non-zero exit. Used to anchor a review range at the divergence point.
    public string? MergeBase(Repo repo, string a, string b)
    {
        if (!IsGitRepo(repo.Path)) return null;
        var output = RunGit(repo.Path, out _, "merge-base", a, b);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    // Resolves the default review base for headRef when the session pins no explicit base: the
    // merge-base with the branch's upstream, falling back to the merge-base with the repo's default
    // branch (origin/HEAD, then a local main/master). Carries the ref it came from + which kind it
    // was, so the header can name the base instead of showing a bare SHA. Null when none resolves —
    // e.g. an orphan branch with no upstream and no default. (Open decision #3: upstream → default → …)
    public ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef)
    {
        if (!IsGitRepo(repo.Path)) return null;
        string? target;
        ReviewBaseKind kind;
        if (GetUpstreamRef(repo.Path, headRef) is { } upstream)
        {
            target = upstream;
            kind = ReviewBaseKind.Upstream;
        }
        else
        {
            target = GetDefaultBranchRef(repo.Path);
            kind = ReviewBaseKind.DefaultBranch;
        }
        if (target == null) return null;
        var sha = MergeBase(repo, target, headRef);
        return sha == null ? null : new ResolvedReviewBase(sha, target, kind);
    }

    // The upstream (remote-tracking) ref of branchRef, e.g. "origin/main", or null when the
    // branch has no configured upstream (a local-only or remote-tracking head).
    private string? GetUpstreamRef(string repoPath, string branchRef)
    {
        var output = RunGit(repoPath, out _, "rev-parse", "--abbrev-ref",
            "--symbolic-full-name", branchRef + "@{upstream}");
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    // The repo's default branch: origin/HEAD's target (e.g. "origin/main") for a cloned repo,
    // else a local "main"/"master" when no remote default exists. Null when none is found.
    private string? GetDefaultBranchRef(string repoPath)
    {
        var originHead = RunGit(repoPath, out _, "symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD");
        if (!string.IsNullOrWhiteSpace(originHead)) return originHead.Trim();
        foreach (var name in DefaultBranchCandidates)
        {
            var verified = RunGit(repoPath, out _, "rev-parse", "--verify", "-q", "refs/heads/" + name);
            if (!string.IsNullOrWhiteSpace(verified)) return name;
        }
        return null;
    }

    private static readonly string[] DefaultBranchCandidates = { "main", "master" };

    // Creates an annotated tag when a message is supplied (`git tag -a <name> -m <msg> <sha>`),
    // otherwise a lightweight tag (`git tag <name> <sha>`). When pushToAllRemotes is set, the new
    // tag ref is pushed to every configured remote; the first push failure aborts and is reported
    // (the local tag has already been created at that point).
    public GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes)
        => RunOperation(repo, () =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new GitOutcome.Failed("Tag name is required.");

            var tagArgs = new List<string> { "tag" };
            if (!string.IsNullOrWhiteSpace(message))
            {
                tagArgs.Add("-a");
                tagArgs.Add(name);
                tagArgs.Add("-m");
                tagArgs.Add(message);
            }
            else
            {
                tagArgs.Add(name);
            }
            tagArgs.Add(commitSha);

            var (tagged, tagError) = RunMutation(repo.Path, tagArgs);
            if (!tagged) return new GitOutcome.Failed(tagError ?? "Failed to create tag.");

            if (pushToAllRemotes)
            {
                foreach (var remote in GetRemoteNames(repo))
                {
                    var (pushed, pushError) = RunMutation(repo.Path, new[] { "push", remote, "refs/tags/" + name });
                    if (!pushed) return new GitOutcome.Failed(pushError ?? $"Failed to push tag to '{remote}'.");
                }
            }

            return GitOutcome.Ok;
        });

    // Deletes a tag locally (`git tag -d`). When deleteFromRemotes is set, the tag is also
    // removed from every configured remote (`git push <remote> --delete refs/tags/<name>`) —
    // mirroring CreateTag's push-to-all-remotes loop. Local deletion happens first; a later
    // remote failure is surfaced but the local tag is already gone.
    public GitOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes)
        => RunOperation(repo, () =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new GitOutcome.Failed("Tag name is required.");

            var deleted = Mutate(repo.Path, "tag", "-d", name);
            if (deleted is GitOutcome.Failed) return deleted;

            if (deleteFromRemotes)
            {
                foreach (var remote in GetRemoteNames(repo))
                {
                    var pushed = Mutate(repo.Path, "push", remote, "--delete", "refs/tags/" + name);
                    if (pushed is GitOutcome.Failed) return pushed;
                }
            }

            return GitOutcome.Ok;
        });

    // `git branch -m` (or -M with force) renames a local branch in-place. Allowed on the
    // currently-checked-out branch — git updates HEAD's symbolic ref to point at the new name.
    public GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force)
        => RunSimple(repo, "git branch", "branch", force ? "-M" : "-m", oldName, newName);

    public MergePreviewResult PreviewMerge(Repo repo, string sourceRef)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new MergePreviewResult(MergePreviewState.Unknown, "Not a git repository.");

            // git 2.38+: real-merge mode. Exit 0 = clean, 1 = conflicts, >1 = error
            // (old git, missing ref, no merge base, etc). Treat errors as Unknown so the
            // dialog quietly skips the preview rather than blocking the user from merging.
            var result = _runner.Run(
                repo.Path,
                new[] { "merge-tree", "--write-tree", "--no-messages", "HEAD", sourceRef },
                GitProcessRunner.GitLaunch.Direct);
            if (!result.Started) return new MergePreviewResult(MergePreviewState.Unknown, "Failed to start git.");

            return result.ExitCode switch
            {
                0 => new MergePreviewResult(MergePreviewState.Clean, null),
                1 => new MergePreviewResult(MergePreviewState.Conflicts, null),
                _ => new MergePreviewResult(MergePreviewState.Unknown, GitProcessRunner.FirstMeaningfulLine(result.Stderr)),
            };
        }
        catch (Exception ex)
        {
            return new MergePreviewResult(MergePreviewState.Unknown, ex.Message);
        }
    }

    // `git merge <ref>` against HEAD. Conflicts produce a non-zero exit but git still
    // writes MERGE_HEAD and stages the resolvable hunks — surface that as "success with
    // conflicts" so the caller can refresh and let the operation banner take over.
    public MergeLikeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy)
        => RunMergeLike(repo, () =>
        {
            var args = new List<string> { "merge" };
            switch (strategy)
            {
                case MergeStrategy.NoFastForward: args.Add("--no-ff"); break;
                case MergeStrategy.FastForwardOnly: args.Add("--ff-only"); break;
                case MergeStrategy.Squash: args.Add("--squash"); break;
            }
            args.Add(sourceRef);

            var result = _runner.Run(repo.Path, args);
            if (result.Ok) return MergeLikeOutcome.Ok;

            // Conflict path: MERGE_HEAD exists in the per-worktree gitdir.
            // --squash and --ff-only never create MERGE_HEAD, so failures there are
            // always real errors.
            if (strategy != MergeStrategy.Squash && strategy != MergeStrategy.FastForwardOnly)
            {
                try
                {
                    var gitDir = GetGitDir(repo.Path);
                    if (gitDir != null && File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                        return new MergeLikeOutcome.Conflicted();
                }
                catch { /* fall through to error */ }
            }

            return new MergeLikeOutcome.Failed(result.BlockError("git merge"));
        });

    // Same merge-tree probe as PreviewMerge — git's three-way merge between HEAD and the
    // target is a reasonable approximation of the conflict landscape a rebase will hit,
    // even though rebase actually replays each commit individually. Good enough to give
    // the user a green/amber heads-up; the real outcome surfaces via the rebase op banner.
    public RebasePreviewResult PreviewRebase(Repo repo, string targetRef)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new RebasePreviewResult(RebasePreviewState.Unknown, "Not a git repository.");

            var result = _runner.Run(
                repo.Path,
                new[] { "merge-tree", "--write-tree", "--no-messages", targetRef, "HEAD" },
                GitProcessRunner.GitLaunch.Direct);
            if (!result.Started) return new RebasePreviewResult(RebasePreviewState.Unknown, "Failed to start git.");

            return result.ExitCode switch
            {
                0 => new RebasePreviewResult(RebasePreviewState.Clean, null),
                1 => new RebasePreviewResult(RebasePreviewState.Conflicts, null),
                _ => new RebasePreviewResult(RebasePreviewState.Unknown, GitProcessRunner.FirstMeaningfulLine(result.Stderr)),
            };
        }
        catch (Exception ex)
        {
            return new RebasePreviewResult(RebasePreviewState.Unknown, ex.Message);
        }
    }

    // `git rebase <target>` replays HEAD's commits onto <target>. With --autostash, git
    // stashes a dirty working tree before the rebase and pops it after success — that
    // covers the "Stash and reapply local changes" checkbox in the dialog. Conflicts produce
    // a non-zero exit but leave rebase-apply/ or rebase-merge/ behind, which the operation
    // banner detects via GetOperationState — surface that as "success with conflicts" so the
    // caller refreshes and the banner takes over.
    public MergeLikeOutcome Rebase(Repo repo, string targetRef, bool autostash)
        => RunMergeLike(repo, () =>
        {
            var args = new List<string> { "rebase" };
            if (autostash) args.Add("--autostash");
            args.Add(targetRef);

            var result = _runner.Run(repo.Path, args);
            if (result.Ok) return MergeLikeOutcome.Ok;

            // Conflict path: rebase leaves rebase-apply/ or rebase-merge/ in the
            // per-worktree gitdir. If either exists, treat the failure as a successful
            // start that produced conflicts — the operation banner will guide the user
            // through resolve/continue/abort.
            try
            {
                var gitDir = GetGitDir(repo.Path);
                if (gitDir != null
                    && (Directory.Exists(Path.Combine(gitDir, "rebase-apply"))
                        || Directory.Exists(Path.Combine(gitDir, "rebase-merge"))))
                {
                    return new MergeLikeOutcome.Conflicted();
                }
            }
            catch { /* fall through to error */ }

            return new MergeLikeOutcome.Failed(result.BlockError("git rebase"));
        });

    // `git cherry-pick <sha>` replays the named commit's changes onto HEAD as a new commit.
    // Conflicts produce a non-zero exit but leave CHERRY_PICK_HEAD in the per-worktree gitdir,
    // which the operation banner detects via GetOperationState — surface that as "success with
    // conflicts" so the caller refreshes and the banner guides resolve/continue/abort. Mirrors
    // the Merge/Rebase conflict handling.
    public MergeLikeOutcome CherryPick(Repo repo, string commitSha)
        => RunMergeLike(repo, () =>
        {
            var result = _runner.Run(repo.Path, new[] { "cherry-pick", commitSha });
            if (result.Ok) return MergeLikeOutcome.Ok;

            try
            {
                var gitDir = GetGitDir(repo.Path);
                if (gitDir != null && File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
                    return new MergeLikeOutcome.Conflicted();
            }
            catch { /* fall through to error */ }

            return new MergeLikeOutcome.Failed(result.BlockError("git cherry-pick"));
        });

    // `git revert --no-edit <sha>` creates a new commit that undoes the named commit. --no-edit
    // keeps it non-interactive (git would otherwise open an editor for the generated message).
    // Conflicts leave REVERT_HEAD behind — same success-with-conflicts handling as cherry-pick
    // so the operation banner takes over.
    public MergeLikeOutcome RevertCommit(Repo repo, string commitSha)
        => RunMergeLike(repo, () =>
        {
            var result = _runner.Run(repo.Path, new[] { "revert", "--no-edit", commitSha });
            if (result.Ok) return MergeLikeOutcome.Ok;

            try
            {
                var gitDir = GetGitDir(repo.Path);
                if (gitDir != null && File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                    return new MergeLikeOutcome.Conflicted();
            }
            catch { /* fall through to error */ }

            return new MergeLikeOutcome.Failed(result.BlockError("git revert"));
        });

    // `git branch -d` refuses to delete a branch not fully merged into its upstream/HEAD;
    // `-D` force-deletes regardless. Also refuses to delete the currently-checked-out branch
    // — callers should gate that in the UI rather than relying on the error.
    public GitOutcome DeleteBranch(Repo repo, string name, bool force)
        => RunSimple(repo, "git branch", "branch", force ? "-D" : "-d", name);

    // Shells out to `git push <remote> --delete <branch>`. The local copy is unaffected.
    // Server may refuse for protected refs — we surface whatever git reports.
    public GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName)
        => RunSimple(repo, "git push", "push", remoteName, "--delete", branchName);

    public GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths)
        => RunOperation(repo, () =>
        {
            var args = new List<string> { "stash", "push" };
            if (includeUntracked) args.Add("--include-untracked");
            if (keepIndex) args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(message);
            }
            if (paths.Count == 0)
                return ToOutcome(_runner.Run(repo.Path, args), "git stash push");

            // Chunking is not an option here: each `git stash push -- <batch>` would create
            // its own stash entry. Without --pathspec-from-file support, refuse a selection
            // that doesn't fit one command line instead of silently splitting the stash.
            if (!GitProcessRunner.SupportsPathspecFromFile
                && ChunkPathsForCommandLine(paths).Skip(1).Any())
                return new GitOutcome.Failed("Stashing this many files at once requires Git 2.26 or newer.");

            return ToOutcome(RunPathspecOp(repo.Path, args, paths), "git stash push");
        });

    public MergeLikeOutcome ApplyStash(Repo repo, int index)
        => RunMergeLike(repo, () =>
        {
            // Snapshot the pre-apply index state. The "apply succeeded with conflicts"
            // heuristic below relies on the transition from clean → unmerged to decide
            // whether the non-zero exit is benign — if the index was already unmerged
            // (e.g. from an earlier failed apply the user hasn't cleared), the post-
            // apply check can't distinguish "this apply produced conflicts" from
            // "those leftover conflicts are still there" and we'd silently swallow
            // the real failure ("untracked file would be overwritten", etc).
            var wasFullyMerged = !HasUnmergedPaths(repo.Path);

            var result = _runner.Run(repo.Path, new[] { "stash", "apply", $"stash@{{{index}}}" });
            if (result.Ok) return MergeLikeOutcome.Ok;

            // `git stash apply` exits 1 when the apply itself worked but produced
            // merge conflicts — the user's stash is on disk, the conflicts are visible
            // in the index, and there's nothing to "fix" about the apply itself. Treat
            // that as Conflicted so the caller can refresh and show the banner instead
            // of an error dialog. Gate on wasFullyMerged so a real failure on a repo
            // that already had conflicts still surfaces its error.
            if (wasFullyMerged && HasUnmergedPaths(repo.Path))
                return new MergeLikeOutcome.Conflicted();
            return new MergeLikeOutcome.Failed(result.BlockError("git stash apply"));
        });

    public GitOutcome DropStash(Repo repo, int index)
        => RunSimple(repo, "git stash drop", "stash", "drop", $"stash@{{{index}}}");

    public GitOutcome RenameStash(Repo repo, int index, string newMessage)
        => RunOperation(repo, () =>
        {
            // git has no native stash rename. Resolve the stash commit, drop the entry,
            // then re-store it under the new message. `git stash store` pushes the entry
            // back onto refs/stash, so a renamed stash moves to the top (stash@{0}).
            var sha = RunGit(repo.Path, out _, "rev-parse", $"stash@{{{index}}}")?.Trim();
            if (string.IsNullOrEmpty(sha))
                return new GitOutcome.Failed("Could not resolve stash commit.");

            var dropped = ToOutcome(_runner.Run(repo.Path, new[] { "stash", "drop", $"stash@{{{index}}}" }), "git stash drop");
            if (dropped is GitOutcome.Failed) return dropped;

            return ToOutcome(_runner.Run(repo.Path, new[] { "stash", "store", "-m", newMessage, sha }), "git stash store");
        });

    public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary)
    {
        try
        {
            if (!IsGitRepo(primary.Path)) return Array.Empty<WorktreeInfo>();
            var stdout = RunGit(primary.Path, out var err, "worktree", "list", "--porcelain");
            return err != null ? Array.Empty<WorktreeInfo>() : ParseWorktreePorcelain(stdout);
        }
        catch
        {
            return Array.Empty<WorktreeInfo>();
        }
    }

    // Porcelain format: blank-line-separated records, one field per line.
    //   worktree <abs-path>
    //   HEAD <sha>     OR omitted for bare
    //   branch refs/heads/<name>  OR  detached  OR  bare
    //   locked [reason]            (optional)
    //   prunable [reason]          (optional)
    private static IReadOnlyList<WorktreeInfo> ParseWorktreePorcelain(string text)
    {
        var results = new List<WorktreeInfo>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        string? path = null, head = null, branch = null, lockReason = null, prunableReason = null;
        bool detached = false, bare = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is null) return;
            results.Add(new WorktreeInfo(
                Path: path,
                HeadSha: head,
                Branch: branch,
                IsDetached: detached,
                IsBare: bare,
                IsLocked: locked,
                LockReason: lockReason,
                IsPrunable: prunable,
                PrunableReason: prunableReason));
            path = null; head = null; branch = null; lockReason = null; prunableReason = null;
            detached = false; bare = false; locked = false; prunable = false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                path = line.Substring("worktree ".Length).Trim();
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                head = line.Substring("HEAD ".Length).Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line.Substring("branch ".Length).Trim();
                const string prefix = "refs/heads/";
                branch = refName.StartsWith(prefix, StringComparison.Ordinal) ? refName.Substring(prefix.Length) : refName;
            }
            else if (line.Equals("detached", StringComparison.Ordinal)) detached = true;
            else if (line.Equals("bare", StringComparison.Ordinal)) bare = true;
            else if (line.Equals("locked", StringComparison.Ordinal)) { locked = true; }
            else if (line.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
                lockReason = line.Substring("locked ".Length).Trim();
            }
            else if (line.Equals("prunable", StringComparison.Ordinal)) { prunable = true; }
            else if (line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
                prunableReason = line.Substring("prunable ".Length).Trim();
            }
        }
        Flush();
        return results;
    }

    public GitOutcome AddWorktree(Repo primary, WorktreeAddRequest request)
        => RunOperation(primary, () =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return new GitOutcome.Failed("Worktree path is required.");
            if (string.IsNullOrWhiteSpace(request.StartPoint))
                return new GitOutcome.Failed("Start point is required.");

            var args = new List<string> { "worktree", "add" };
            if (request.Force) args.Add("--force");
            if (!string.IsNullOrWhiteSpace(request.NewBranchName))
            {
                args.Add("-b");
                args.Add(request.NewBranchName!);
            }
            args.Add(request.Path);
            args.Add(request.StartPoint);
            return ToOutcome(_runner.Run(primary.Path, args), "git worktree add");
        });

    public GitOutcome RemoveWorktree(Repo primary, string worktreePath, bool force)
        => RunOperation(primary, () =>
        {
            if (string.IsNullOrWhiteSpace(worktreePath))
                return new GitOutcome.Failed("Worktree path is required.");

            var args = new List<string> { "worktree", "remove" };
            if (force) args.Add("--force");
            args.Add(worktreePath);
            return ToOutcome(_runner.Run(primary.Path, args), "git worktree remove");
        });

    public GitOutcome PruneWorktrees(Repo primary)
        => RunSimple(primary, "git worktree prune", "worktree", "prune");

    // ────────── submodules ──────────

    public IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary)
    {
        try
        {
            if (!IsGitRepo(primary.Path)) return Array.Empty<SubmoduleInfo>();
            if (!File.Exists(System.IO.Path.Combine(primary.Path, ".gitmodules")))
                return Array.Empty<SubmoduleInfo>();

            var byName = ParseGitmodules(primary.Path);
            if (byName == null) return Array.Empty<SubmoduleInfo>();

            var statusByPath = ParseSubmoduleStatus(primary.Path);
            var recordedByPath = ParseRecordedSubmodulePointers(primary.Path);
            return BuildSubmoduleInfos(primary, byName, statusByPath, recordedByPath);
        }
        catch
        {
            return Array.Empty<SubmoduleInfo>();
        }
    }

    // Logical entries from .gitmodules. Each `submodule.<name>.path` row gives us one submodule;
    // .url and .branch hang off the same <name>. Returns null on a git config failure.
    private Dictionary<string, (string? Path, string? Url, string? Branch)>? ParseGitmodules(string repoPath)
    {
        var configOut = RunGit(repoPath, out var cfgErr, "config", "--file", ".gitmodules", "--list");
        if (cfgErr != null) return null;

        var byName = new Dictionary<string, (string? Path, string? Url, string? Branch)>(StringComparer.Ordinal);
        foreach (var raw in configOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("submodule.", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line.Substring(0, eq);
            var value = line.Substring(eq + 1);
            var lastDot = key.LastIndexOf('.');
            if (lastDot < 0) continue;
            var nameStart = "submodule.".Length;
            var nameLen = lastDot - nameStart;
            if (nameLen <= 0) continue;
            var name = key.Substring(nameStart, nameLen);
            var field = key.Substring(lastDot + 1);
            byName.TryGetValue(name, out var entry);
            entry = field switch
            {
                "path"   => (value, entry.Url, entry.Branch),
                "url"    => (entry.Path, value, entry.Branch),
                "branch" => (entry.Path, entry.Url, value),
                _        => entry,
            };
            byName[name] = entry;
        }
        return byName;
    }

    // Per-path status + describe + current SHA from `git submodule status`. Per line
    // '<flag><sha> <path> (<describe>)' — flag ' ' = up-to-date, '+' = modified, '-' = not
    // initialized, 'U' = conflict.
    private Dictionary<string, (char Flag, string? Sha, string? Describe)> ParseSubmoduleStatus(string repoPath)
    {
        var statusOut = RunGit(repoPath, out _, "submodule", "status");
        var statusByPath = new Dictionary<string, (char Flag, string? Sha, string? Describe)>(StringComparer.Ordinal);
        foreach (var raw in (statusOut ?? string.Empty).Split('\n'))
        {
            if (raw.Length < 2) continue;
            var flag = raw[0];
            var rest = raw.Substring(1);
            var sp = rest.IndexOf(' ');
            if (sp < 0) continue;
            var sha = rest.Substring(0, sp);
            var afterSha = rest.Substring(sp + 1);
            string pathPart;
            string? describe = null;
            var parenIdx = afterSha.LastIndexOf(" (", StringComparison.Ordinal);
            if (parenIdx >= 0 && afterSha.EndsWith(")", StringComparison.Ordinal))
            {
                pathPart = afterSha.Substring(0, parenIdx);
                describe = afterSha.Substring(parenIdx + 2, afterSha.Length - parenIdx - 3);
            }
            else
            {
                pathPart = afterSha;
            }
            statusByPath[NormalizeRelPath(pathPart)] = (flag, sha, describe);
        }
        return statusByPath;
    }

    // Authoritative recorded SHA via `git ls-tree HEAD` — submodule status's SHA reports the
    // CURRENT checkout (or a leading + when modified), not what the parent's HEAD tree actually
    // records. ls-tree gives the recorded pointer directly.
    private Dictionary<string, string> ParseRecordedSubmodulePointers(string repoPath)
    {
        var lsTreeOut = RunGit(repoPath, out _, "ls-tree", "-r", "HEAD");
        var recordedByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (lsTreeOut ?? string.Empty).Split('\n'))
        {
            // <mode> SP <type> SP <sha> TAB <path>; gitlinks have mode 160000.
            if (!raw.StartsWith("160000 ", StringComparison.Ordinal)) continue;
            var tab = raw.IndexOf('\t');
            if (tab < 0) continue;
            var meta = raw.Substring(0, tab);
            var pathPart = raw.Substring(tab + 1);
            var parts = meta.Split(' ');
            if (parts.Length < 3) continue;
            recordedByPath[NormalizeRelPath(pathPart)] = parts[2];
        }
        return recordedByPath;
    }

    private static List<SubmoduleInfo> BuildSubmoduleInfos(
        Repo primary,
        Dictionary<string, (string? Path, string? Url, string? Branch)> byName,
        Dictionary<string, (char Flag, string? Sha, string? Describe)> statusByPath,
        Dictionary<string, string> recordedByPath)
    {
        var results = new List<SubmoduleInfo>(byName.Count);
        foreach (var (_, entry) in byName)
        {
            if (entry.Path is null) continue;
            var rel = NormalizeRelPath(entry.Path);
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(primary.Path, rel));
            recordedByPath.TryGetValue(rel, out var recorded);
            statusByPath.TryGetValue(rel, out var status);
            var smStatus = status.Flag switch
            {
                '+' => SubmoduleStatus.Modified,
                '-' => SubmoduleStatus.NotInitialized,
                'U' => SubmoduleStatus.MergeConflict,
                _   => SubmoduleStatus.UpToDate,
            };
            results.Add(new SubmoduleInfo(
                Path: rel,
                AbsolutePath: abs,
                Url: entry.Url,
                Branch: entry.Branch,
                RecordedSha: recorded,
                CurrentSha: smStatus == SubmoduleStatus.NotInitialized ? null : status.Sha,
                Status: smStatus,
                Describe: status.Describe));
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return results;
    }

    public GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request)
        => RunOperation(primary, () =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
                return new GitOutcome.Failed("Submodule URL is required.");
            if (string.IsNullOrWhiteSpace(request.Path))
                return new GitOutcome.Failed("Submodule path is required.");

            var args = new List<string> { "submodule", "add" };
            if (request.Force) args.Add("--force");
            if (!string.IsNullOrWhiteSpace(request.Branch))
            {
                args.Add("-b");
                args.Add(request.Branch!);
            }
            args.Add(request.Url);
            args.Add(request.Path);
            return ToOutcome(_runner.Run(primary.Path, args), $"git {string.Join(' ', args)}");
        });

    public MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request)
        => RunMergeLike(primary, () =>
        {
            var args = new List<string> { "submodule", "update" };
            if (request.Init) args.Add("--init");
            if (request.Recursive) args.Add("--recursive");
            switch (request.Mode)
            {
                case SubmoduleUpdateMode.Merge:  args.Add("--merge");  break;
                case SubmoduleUpdateMode.Rebase: args.Add("--rebase"); break;
            }
            if (request.Paths is { Count: > 0 })
            {
                args.Add("--");
                foreach (var p in request.Paths) args.Add(p);
            }

            var result = _runner.Run(primary.Path, args);
            if (result.Ok)
            {
                ReattachSubmodulesOnBranchTip(primary);
                return MergeLikeOutcome.Ok;
            }
            // Merge/rebase strategies surface CONFLICT markers in stdout when they fail —
            // hand that signal up so the dialog can show a "see Operation banner" hint
            // instead of just a raw error.
            var combined = result.Stdout + "\n" + result.Stderr;
            var conflicts = combined.Contains("CONFLICT", StringComparison.Ordinal)
                            || combined.Contains("merge conflict", StringComparison.OrdinalIgnoreCase);
            return conflicts
                ? new MergeLikeOutcome.Conflicted()
                : new MergeLikeOutcome.Failed(result.BlockError("git submodule update"));
        });

    public GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force)
        => RunOperation(primary, () =>
        {
            if (string.IsNullOrWhiteSpace(submodulePath))
                return new GitOutcome.Failed("Submodule path is required.");

            // Two-step: deinit frees the working tree + .git/modules entry; rm removes
            // the gitlink and the .gitmodules entry, staging the change as a commit-ready
            // deletion. Both happen under the same lock so the user sees one atomic op.
            var deinitArgs = new List<string> { "submodule", "deinit" };
            if (force) deinitArgs.Add("--force");
            deinitArgs.Add("--");
            deinitArgs.Add(submodulePath);
            var deinited = Mutate(primary.Path, deinitArgs.ToArray());
            if (deinited is GitOutcome.Failed) return deinited;

            var rmArgs = new List<string> { "rm" };
            if (force) rmArgs.Add("-f");
            rmArgs.Add("--");
            rmArgs.Add(submodulePath);
            return Mutate(primary.Path, rmArgs.ToArray());
        });

    public bool StageSubmodulePointer(Repo parent, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            if (!IsGitRepo(parent.Path)) return false;

            var rel = NormalizeRelPath(relativePath);
            if (rel.Length == 0 || rel == ".") return false;

            using var _ = LockRepo(parent.Path);

            // --ignore-submodules=dirty so only a moved HEAD commit counts, not uncommitted
            // changes inside the submodule's working tree (which `git add` wouldn't record
            // anyway). Exit 0 => the gitlink already matches HEAD, nothing to stage.
            var diff = _runner.Run(parent.Path, new[] { "diff", "--quiet", "--ignore-submodules=dirty", "HEAD", "--", rel });
            if (diff.Ok) return false;

            var (ok, _err) = RunMutation(parent.Path, new[] { "add", "--", rel });
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha)
    {
        try
        {
            if (!IsGitRepo(repo.Path) || string.IsNullOrWhiteSpace(commitSha))
                return Array.Empty<SubmodulePointerChange>();

            // diff-tree raw output: ":<src-mode> <dst-mode> <src-sha> <dst-sha> <status>\t<path>"
            // --root makes the first commit produce its own additions instead of erroring.
            var rawOut = RunGit(repo.Path, out var err, "diff-tree", "-r", "--no-commit-id",
                "--root", "--raw", commitSha);
            if (err != null || string.IsNullOrEmpty(rawOut))
                return Array.Empty<SubmodulePointerChange>();

            var results = new List<SubmodulePointerChange>();
            foreach (var raw in rawOut.Split('\n'))
            {
                var change = ParseSubmodulePointerLine(repo, raw);
                if (change != null) results.Add(change);
            }
            return results;
        }
        catch
        {
            return Array.Empty<SubmodulePointerChange>();
        }
    }

    private SubmodulePointerChange? ParseSubmodulePointerLine(Repo repo, string raw)
    {
        if (!raw.StartsWith(":", StringComparison.Ordinal)) return null;
        var tab = raw.IndexOf('\t');
        if (tab < 0) return null;
        var meta = raw.Substring(1, tab - 1);
        var pathPart = raw.Substring(tab + 1);
        var parts = meta.Split(' ');
        if (parts.Length < 5) return null;
        var srcMode = parts[0];
        var dstMode = parts[1];
        // Only gitlink entries (160000 on either side).
        if (srcMode != "160000" && dstMode != "160000") return null;
        var srcSha = parts[2];
        var dstSha = parts[3];

        var ahead = 0;
        var behind = 0;
        string? shortLog = null;
        var subPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(repo.Path, pathPart));
        // Only resolve range info when the submodule is initialized locally AND both ends are real
        // commits — added/removed entries have a 40-zero sentinel on one side that no rev-list
        // query can resolve.
        if (!IsAllZeros(srcSha) && !IsAllZeros(dstSha) && Directory.Exists(subPath) && IsGitRepo(subPath))
            ResolveSubmoduleRange(subPath, srcSha, dstSha, out ahead, out behind, out shortLog);

        return new SubmodulePointerChange(
            Path: NormalizeRelPath(pathPart),
            FromSha: srcSha,
            ToSha: dstSha,
            AheadCount: ahead,
            BehindCount: behind,
            ShortLog: shortLog);
    }

    private void ResolveSubmoduleRange(
        string subPath, string srcSha, string dstSha, out int ahead, out int behind, out string? shortLog)
    {
        ahead = 0;
        behind = 0;
        shortLog = null;
        var rl = RunGit(subPath, out _, "rev-list", "--left-right", "--count", $"{srcSha}...{dstSha}");
        if (rl != null)
        {
            var rlParts = rl.Trim().Split('\t');
            if (rlParts.Length == 2)
            {
                int.TryParse(rlParts[0], out behind);
                int.TryParse(rlParts[1], out ahead);
            }
        }
        var log = RunGit(subPath, out _, "log", "--oneline", "--no-decorate", "-n", "20", $"{srcSha}..{dstSha}");
        if (!string.IsNullOrWhiteSpace(log)) shortLog = log;
    }

    // Writes an identity into the repo's --local config ("pin to repo"). After this the resolver
    // sees an explicit local user.email and backs off injection, so GUI and terminal commits use
    // the same author. Writes the SAME set of keys injection would apply (SSH command, signing),
    // and UNSETS the ones this profile doesn't use — so re-pinning a leaner profile can't leave a
    // previous profile's SSH key or signing config behind.
    public GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config)
        => RunOperation(repo, () =>
        {
            foreach (var (key, value) in config.Entries())
            {
                var (ok, err) = value != null
                    ? RunMutation(repo.Path, new[] { "config", "--local", key, value })
                    : UnsetLocalConfig(repo.Path, key);
                if (!ok) return new GitOutcome.Failed(err!);
            }
            return GitOutcome.Ok;
        });

    // `git config --local --unset` exits 5 when the key was already absent — that's the desired
    // end state, not a failure, so it's treated as success.
    private (bool Ok, string? Error) UnsetLocalConfig(string repoPath, string key)
    {
        var result = _runner.Run(repoPath, new[] { "config", "--local", "--unset", key });
        if (result.Ok || result.ExitCode == 5) return (true, null);
        return (false, result.BlockError($"git config --local --unset {key}"));
    }

    // Small shared helper for "spawn git, return (ok, errorOrNull)". Used where multiple
    // successive mutations need to be sequenced inside a single repo lock.
    private (bool Ok, string? Error) RunMutation(string repoPath, IReadOnlyList<string> args)
    {
        var result = _runner.Run(repoPath, args);
        return result.Ok ? (true, null) : (false, result.BlockError($"git {string.Join(' ', args)}"));
    }

    // Owns the not-a-repo guard, the repo lock, and the exception fold shared by every
    // mutating operation; `fail` builds the hierarchy-specific failure case.
    private T RunLocked<T>(Repo repo, Func<T> body, Func<string, T> fail)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return fail("Not a git repository.");
            using var _ = LockRepo(repo.Path);
            return body();
        }
        catch (Exception ex)
        {
            return fail(ex.Message);
        }
    }

    private GitOutcome RunOperation(Repo repo, Func<GitOutcome> body)
        => RunLocked(repo, body, static m => new GitOutcome.Failed(m));

    private MergeLikeOutcome RunMergeLike(Repo repo, Func<MergeLikeOutcome> body)
        => RunLocked(repo, body, static m => new MergeLikeOutcome.Failed(m));

    private GitOutcome RunSimple(Repo repo, string label, params string[] args)
        => RunOperation(repo, () => ToOutcome(_runner.Run(repo.Path, args), label));

    private static GitOutcome ToOutcome(GitProcessRunner.GitResult result, string label)
        => result.Ok ? GitOutcome.Ok : new GitOutcome.Failed(result.BlockError(label));

    private GitOutcome Mutate(string repoPath, params string[] args)
    {
        var (ok, err) = RunMutation(repoPath, args);
        return ok ? GitOutcome.Ok : new GitOutcome.Failed(err!);
    }

    // Bulk file operations must never put an unbounded path list on the command line:
    // Windows CreateProcess caps the whole line at 32,767 chars, so "Stage All" in a repo
    // with a few hundred changed files fails to even spawn git ("The filename or extension
    // is too long"). Git >= 2.26 takes the list NUL-separated on stdin; older gits get it
    // chunked into command lines that stay under the cap.
    private GitProcessRunner.GitResult RunPathspecOp(string repoPath, IReadOnlyList<string> preArgs, IReadOnlyList<string> paths)
    {
        if (GitProcessRunner.SupportsPathspecFromFile)
        {
            var args = new List<string>(preArgs.Count + 2);
            args.AddRange(preArgs);
            args.Add("--pathspec-from-file=-");
            args.Add("--pathspec-file-nul");
            return _runner.Run(repoPath, args, GitProcessRunner.GitLaunch.Direct, string.Join('\0', paths));
        }

        var last = new GitProcessRunner.GitResult(0, string.Empty, string.Empty);
        foreach (var batch in ChunkPathsForCommandLine(paths))
        {
            var args = new List<string>(preArgs.Count + 1 + batch.Count);
            args.AddRange(preArgs);
            args.Add("--");
            args.AddRange(batch);
            last = _runner.Run(repoPath, args, GitProcessRunner.GitLaunch.Direct);
            if (!last.Ok) return last;
        }
        return last;
    }

    // Budget leaves headroom under the 32,767-char cap for the git path, subcommand args,
    // identity `-c` prefix, and per-arg quoting.
    internal const int PathspecCommandLineBudget = 28_000;

    internal static IEnumerable<IReadOnlyList<string>> ChunkPathsForCommandLine(IReadOnlyList<string> paths)
    {
        var batch = new List<string>();
        var length = 0;
        foreach (var p in paths)
        {
            if (batch.Count > 0 && length + p.Length + 3 > PathspecCommandLineBudget)
            {
                yield return batch;
                batch = new List<string>();
                length = 0;
            }
            batch.Add(p);
            length += p.Length + 3;
        }
        if (batch.Count > 0) yield return batch;
    }

    private static string NormalizeRelPath(string p) => p.Replace('\\', '/').TrimEnd('/');

    private static bool IsAllZeros(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] != '0') return false;
        return s.Length > 0;
    }

    private GitOutcome RunGitCheckout(string repoPath, IReadOnlyList<string> gitArgs)
        => ToOutcome(_runner.Run(repoPath, gitArgs), "git checkout");

    // LibGit2Sharp's Patch API drives diff output through native→managed callbacks (per
    // hunk and per line), which the NativeAOT-generated marshalling stubs for GitDiffHunk
    // NRE on. Everything else in libgit2 we use is fine; only diff goes through this
    // callback path. Shell out to `git diff` for diffs to sidestep it entirely.
    public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return DiffError(repo, path, side, "Not a git repository.");

            var patchText = RunDiffForSide(repo, path, side, commitSha, baseSha, out var error);
            if (patchText == null)
                return DiffError(repo, path, side, error ?? "git diff failed.");

            var result = ParseGitDiff(repo.Id, path, side, patchText);
            // LFS status only matters for binary files (the diff body is hidden, so the badge is
            // the only place the user learns how the blob is stored). Querying check-attr is an
            // extra git invocation, so we skip it for ordinary text diffs.
            if (result.IsBinary)
                result = result with { IsLfs = IsLfsTracked(repo.Path, path) };
            return result;
        }
        catch (Exception ex)
        {
            return DiffError(repo, path, side, ex.Message);
        }
    }

    // Runs the right git command for the requested diff side and returns its raw patch text, or
    // null with `error` set on a validation or git failure.
    private string? RunDiffForSide(
        Repo repo, string path, DiffSide side, string? commitSha, string? baseSha, out string? error)
    {
        error = null;
        var contextArg = $"--unified={DiffOptions.ContextLines}";
        switch (side)
        {
            case DiffSide.Commit:
                if (string.IsNullOrEmpty(commitSha))
                {
                    error = "Commit SHA required for commit diff.";
                    return null;
                }
                // `git show` handles root commits and merges correctly; --format= suppresses the
                // commit message header so the output is a plain patch parseable by ParseGitDiff.
                return RunGitDiff(repo.Path, out error,
                    "show", "--no-color", "--format=", "-M", contextArg, commitSha, "--", path);
            case DiffSide.Range:
                if (string.IsNullOrEmpty(commitSha) || string.IsNullOrEmpty(baseSha))
                {
                    error = "Base and head SHAs required for range diff.";
                    return null;
                }
                // The range's net diff for one file: base→head directly (two-dot). base is already
                // the resolved merge-base, so this is the sum of the range's increments for this path.
                return RunGitDiff(repo.Path, out error,
                    "diff", "--no-color", "-M", contextArg, baseSha, commitSha, "--", path);
            case DiffSide.Staged:
                return RunGitDiff(repo.Path, out error,
                    "diff", "--cached", "--no-color", "-M", contextArg, "--", path);
            case DiffSide.WorkingTree:
                // Everything the file has changed since HEAD, index state ignored, so staging a
                // file leaves its diff untouched.
                if (IsTracked(repo.Path, path))
                    return RunGitDiff(repo.Path, out error,
                        "diff", "HEAD", "--no-color", "-M", contextArg, "--", path);
                return RunUntrackedFileDiff(repo, path, contextArg, out error);
            default:
                if (IsTracked(repo.Path, path))
                    return RunGitDiff(repo.Path, out error,
                        "diff", "--no-color", "-M", contextArg, "--", path);
                return RunUntrackedFileDiff(repo, path, contextArg, out error);
        }
    }

    // Untracked file: `git diff` ignores it, so render it as an addition by diffing against the
    // platform null device.
    private string? RunUntrackedFileDiff(Repo repo, string path, string contextArg, out string? error)
    {
        var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var absPath = Path.IsPathRooted(path) ? path : Path.Combine(repo.Path, path);
        return RunGitDiff(repo.Path, out error,
            "diff", "--no-color", "--no-index", contextArg, "--", nullPath, absPath);
    }

    public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return null;

            switch (side)
            {
                case DiffSide.Commit:
                    if (string.IsNullOrEmpty(commitSha)) return null;
                    // old = the commit's first parent; new = the commit itself. A root commit has
                    // no parent, so `<sha>~1:` fails and old comes back null (all-add diff anyway).
                    return ShowBlob(repo.Path, oldSide ? $"{commitSha}~1:{path}" : $"{commitSha}:{path}");

                case DiffSide.Range:
                    // Combined range: old = the base blob, new = the head blob.
                    if (string.IsNullOrEmpty(commitSha) || string.IsNullOrEmpty(baseSha)) return null;
                    return ShowBlob(repo.Path, oldSide ? $"{baseSha}:{path}" : $"{commitSha}:{path}");

                case DiffSide.Staged:
                    // Staged diff is index-vs-HEAD: old = HEAD blob, new = staged (index) blob.
                    return ShowBlob(repo.Path, oldSide ? $"HEAD:{path}" : $":{path}");

                case DiffSide.WorkingTree:
                    // HEAD-vs-disk: old = HEAD blob, new = file on disk.
                    return oldSide ? ShowBlob(repo.Path, $"HEAD:{path}") : ReadWorkingFile(repo.Path, path);

                default: // Unstaged: working-tree-vs-index. old = index blob, new = file on disk.
                    return oldSide ? ShowBlob(repo.Path, $":{path}") : ReadWorkingFile(repo.Path, path);
            }
        }
        catch
        {
            return null;
        }
    }

    // `git show <rev>:<path>` prints a blob's raw contents. Returns null on any non-zero exit
    // (path absent on that side, bad rev, etc.) so the caller falls back to plain rendering.
    private string? ShowBlob(string workingDir, string revPath)
    {
        var result = _runner.Run(workingDir, new[] { "show", revPath }, GitProcessRunner.GitLaunch.Direct);
        return result.Ok ? result.Stdout : null;
    }

    private static string? ReadWorkingFile(string workingDir, string path)
    {
        try
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(workingDir, path);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch
        {
            return null;
        }
    }

    private string RunGit(string workingDir, out string? error, params string[] args)
        => RunGitInternal(workingDir, allowExitCode1: false, out error, args)!;

    // `git diff --no-index` exits 1 when the two inputs differ — that's normal output, not failure.
    private string? RunGitDiff(string workingDir, out string? error, params string[] args)
        => RunGitInternal(workingDir, allowExitCode1: true, out error, args);

    private string? RunGitInternal(string workingDir, bool allowExitCode1, out string? error, string[] args, bool inject = true)
    {
        error = null;
        var result = _runner.Run(workingDir, args, GitProcessRunner.GitLaunch.Direct, inject: inject);
        if (result.Ok || (allowExitCode1 && result.ExitCode == 1)) return result.Stdout;
        error = result.FirstLineError("git");
        return null;
    }

    // ────────── raw (non-injecting) config reads for GitIdentityService ──────────
    // These MUST pass inject:false: the identity resolver calls them, and the runner would
    // otherwise re-enter the resolver (infinite recursion) on every config read.

    bool IGitRawConfigReader.IsRepoAvailable(string repoPath) => IsGitRepo(repoPath);

    IReadOnlyList<string> IGitRawConfigReader.GetRemoteNamesRaw(string repoPath)
    {
        // Surface a git failure (e.g. held lock) as an exception so the resolver treats it as
        // transient rather than memoizing "no remotes". The repo-available check is done once by
        // the resolver before any of these reads, so it isn't repeated here.
        var names = ReadRemoteNames(repoPath, inject: false, out var error);
        if (error != null) throw new IOException($"git remote: {error}");
        return names;
    }

    string? IGitRawConfigReader.GetRemoteUrlRaw(string repoPath, string remoteName)
    {
        // Surface a git failure as an exception so the resolver treats it as transient instead of
        // memoizing "no match" — mirroring GetRemoteNamesRaw. A transient `remote get-url` failure
        // would otherwise pin the repo to the global identity until the next flush.
        var url = ReadRemoteUrl(repoPath, remoteName, inject: false, out var error);
        if (error != null) throw new IOException($"git remote get-url: {error}");
        return url;
    }

    (string? Name, string? Email) IGitRawConfigReader.GetLocalIdentityRaw(string repoPath)
    {
        // --local --get exits 1 when the key is unset; treat that as "not configured". Any OTHER
        // git failure (held lock, etc.) is surfaced as an exception so the resolver treats it as
        // transient — otherwise a momentary read failure would look like "no local identity" and
        // let an auto-matched profile override a deliberately pinned --local identity, then cache it.
        var name = RunGitInternal(repoPath, allowExitCode1: true, out var nameErr, new[] { "config", "--local", "--get", "user.name" }, inject: false)?.Trim();
        if (nameErr != null) throw new IOException($"git config user.name: {nameErr}");
        var email = RunGitInternal(repoPath, allowExitCode1: true, out var emailErr, new[] { "config", "--local", "--get", "user.email" }, inject: false)?.Trim();
        if (emailErr != null) throw new IOException($"git config user.email: {emailErr}");
        return (string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(email) ? null : email);
    }

    void IGitRawConfigReader.AttachIdentityResolver(GitIdentityService identity)
        => _runner.IdentityPrefixResolver = identity.ResolvePrefixArgs;

    private bool IsTracked(string workingDir, string path)
    {
        var result = _runner.Run(
            workingDir,
            new[] { "ls-files", "--error-unmatch", "--", path },
            GitProcessRunner.GitLaunch.Direct);
        return result.Ok;
    }

    // A path is LFS-tracked when .gitattributes assigns it the `lfs` filter. `git check-attr`
    // resolves the attribute the same way the smudge/clean machinery does, so it reflects the
    // effective rule for this path. Output is one line: "<path>: filter: <value>".
    private bool IsLfsTracked(string workingDir, string path)
    {
        var result = _runner.Run(
            workingDir,
            new[] { "check-attr", "filter", "--", path },
            GitProcessRunner.GitLaunch.Direct);
        return result.Ok && result.Stdout.Contains("filter: lfs");
    }

    private static DiffResult ParseGitDiff(Guid repoId, string path, DiffSide side, string patchText)
    {
        if (string.IsNullOrEmpty(patchText))
            return new DiffResult(repoId, path, null, side, false, false, null, null, Array.Empty<DiffHunk>(), false, null);

        string? oldPath = null;
        int? oldMode = null, newMode = null;
        bool isBinary = false;

        foreach (var rawLine in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.StartsWith("@@")) break;
            if (rawLine.StartsWith("rename from "))
                oldPath = rawLine.Substring("rename from ".Length).Trim();
            else if (rawLine.StartsWith("old mode "))
                oldMode = TryParseOctal(rawLine.Substring("old mode ".Length).Trim());
            else if (rawLine.StartsWith("new mode "))
                newMode = TryParseOctal(rawLine.Substring("new mode ".Length).Trim());
            else if (rawLine.StartsWith("Binary files ") || rawLine.StartsWith("GIT binary patch"))
                isBinary = true;
        }

        if (isBinary)
            return new DiffResult(repoId, path, oldPath, side, true, false, oldMode, newMode, Array.Empty<DiffHunk>(), false, null);

        var (hunks, truncated) = ParsePatch(patchText);
        var modesDiffer = oldMode.HasValue && newMode.HasValue && oldMode != newMode;
        var isModeOnly = modesDiffer && hunks.Count == 0;

        return new DiffResult(
            RepoId: repoId,
            Path: path,
            OldPath: oldPath,
            Side: side,
            IsBinary: false,
            IsModeOnly: isModeOnly,
            OldMode: modesDiffer ? oldMode : null,
            NewMode: modesDiffer ? newMode : null,
            Hunks: hunks,
            Truncated: truncated,
            ErrorMessage: null);
    }

    private static int? TryParseOctal(string s)
    {
        try { return Convert.ToInt32(s, 8); }
        catch { return null; }
    }

    private static DiffResult DiffError(Repo repo, string path, DiffSide side, string message)
        => new(repo.Id, path, null, side, false, false, null, null, Array.Empty<DiffHunk>(), false, message);

    private static (IReadOnlyList<DiffHunk> Hunks, bool Truncated) ParsePatch(string patchText)
    {
        if (string.IsNullOrEmpty(patchText))
            return (Array.Empty<DiffHunk>(), false);

        var acc = new PatchAccumulator();
        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
            acc.Consume(raw);
        return acc.Finish();
    }

    // Walks a unified-diff patch line by line, building one hunk at a time. Emitted lines are
    // capped at DiffOptions.TruncationLineCap; past the cap the rest are counted as truncated.
    private sealed class PatchAccumulator
    {
        private readonly List<DiffHunk> _hunks = new();
        private bool _truncated;
        private int _totalLines;

        private int _oldStart, _oldLines, _newStart, _newLines;
        private string? _header;
        private List<DiffLine>? _lines;
        private int _oldCursor, _newCursor;
        private bool _inHunk;

        public void Consume(string raw)
        {
            if (raw.StartsWith("@@"))
            {
                BeginHunk(raw);
                return;
            }
            if (!_inHunk || _lines == null || raw.Length == 0) return;
            if (raw[0] == '\\')
            {
                MarkNoNewlineAtEof();
                return;
            }
            AppendBodyLine(raw);
        }

        public (IReadOnlyList<DiffHunk> Hunks, bool Truncated) Finish()
        {
            Flush();
            return (_hunks, _truncated);
        }

        private void BeginHunk(string raw)
        {
            Flush();
            if (!TryParseHunkHeader(raw, out _oldStart, out _oldLines, out _newStart, out _newLines, out _header))
            {
                _inHunk = false;
                return;
            }
            _lines = new List<DiffLine>();
            _oldCursor = _oldStart;
            _newCursor = _newStart;
            _inHunk = true;
        }

        // "\ No newline at end of file" applies to the line just emitted. Flag it so the patch
        // builder can reproduce the marker rather than silently appending a trailing newline when
        // the hunk is staged/discarded.
        private void MarkNoNewlineAtEof()
        {
            if (_lines!.Count > 0)
                _lines[^1] = _lines[^1] with { NoNewlineAtEof = true };
        }

        private void AppendBodyLine(string raw)
        {
            var text = raw.Length > 1 ? raw[1..] : string.Empty;
            DiffLine? line = raw[0] switch
            {
                ' ' => new DiffLine(DiffLineKind.Context, _oldCursor++, _newCursor++, text),
                '+' => new DiffLine(DiffLineKind.Added, null, _newCursor++, text),
                '-' => new DiffLine(DiffLineKind.Removed, _oldCursor++, null, text),
                _   => null,
            };
            if (line == null) return;
            if (_totalLines >= DiffOptions.TruncationLineCap)
            {
                _truncated = true;
                return;
            }
            _lines!.Add(line);
            _totalLines++;
        }

        private void Flush()
        {
            if (!_inHunk || _lines == null) return;
            _hunks.Add(new DiffHunk(_oldStart, _oldLines, _newStart, _newLines, _header, _lines));
        }
    }

    // Parses "@@ -<oldStart>[,<oldLines>] +<newStart>[,<newLines>] @@ <header?>".
    private static bool TryParseHunkHeader(
        string raw,
        out int oldStart, out int oldLines,
        out int newStart, out int newLines,
        out string? header)
    {
        oldStart = oldLines = newStart = newLines = 0;
        header = null;

        var close = raw.IndexOf("@@", 2, StringComparison.Ordinal);
        if (close < 0) return false;
        var ranges = raw.Substring(2, close - 2).Trim();
        var parts = ranges.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (parts[0].Length < 2 || parts[0][0] != '-') return false;
        if (parts[1].Length < 2 || parts[1][0] != '+') return false;

        if (!TryParseRange(parts[0].AsSpan(1), out oldStart, out oldLines)) return false;
        if (!TryParseRange(parts[1].AsSpan(1), out newStart, out newLines)) return false;

        var afterClose = close + 2;
        if (afterClose < raw.Length)
        {
            var trail = raw[afterClose..].TrimStart();
            if (trail.Length > 0) header = trail;
        }
        return true;
    }

    private static bool TryParseRange(ReadOnlySpan<char> s, out int start, out int count)
    {
        start = 0;
        count = 1;
        var comma = s.IndexOf(',');
        if (comma < 0)
            return int.TryParse(s, out start);
        if (!int.TryParse(s[..comma], out start)) return false;
        if (!int.TryParse(s[(comma + 1)..], out count)) return false;
        return true;
    }

    // Detects whether the repo is mid-operation (merge, rebase, cherry-pick, …) by looking
    // for the well-known sentinel files git drops into .git/ for each. Mirrors what `git
    // status` itself checks; covers worktrees too via libgit2's Info.Path (which points at
    // the per-worktree gitdir, not the main one). Returns None when nothing is in progress
    // or when the repo path is invalid — banner callers treat None as "hide".
    public RepoOperationState GetOperationState(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return RepoOperationState.None;
            var gitDir = GetGitDir(repo.Path);
            if (gitDir == null) return RepoOperationState.None;
            // Defer the unmerged-paths probe until after the sentinel checks: a real
            // in-progress op (rebase, merge, etc.) returns before we need it, so the
            // ls-files call only fires on the fallback path.

            // Order matters only for AM-vs-Rebase: `git am` uses rebase-apply/ too, but adds
            // an `applying` marker. Check the marker before falling through to plain rebase.
            if (Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            {
                if (File.Exists(Path.Combine(gitDir, "rebase-apply", "applying")))
                    return RepoOperationState.ApplyMailbox;
                return RepoOperationState.Rebase;
            }
            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))) return RepoOperationState.Rebase;
            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return RepoOperationState.CherryPick;
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return RepoOperationState.Revert;
            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return RepoOperationState.Merge;
            if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return RepoOperationState.Bisect;

            // No in-progress op, but the index still has unmerged entries — typically a
            // `git stash apply` that conflicted, or a `checkout -m` / `read-tree -m` left
            // partway. Fall back to a generic banner so the user isn't left wondering
            // why their working tree is full of conflict markers.
            return HasUnmergedPaths(repo.Path) ? RepoOperationState.UnmergedPaths : RepoOperationState.None;
        }
        catch
        {
            return RepoOperationState.None;
        }
    }

    // Runs `git <op> --abort` (or the appropriate equivalent) for the in-progress state. For
    // UnmergedPaths — a stash-apply / checkout -m conflict that leaves the index unmerged
    // with no sentinel — `reset --merge` is the documented recovery: discards conflicting
    // worktree changes and clears the unmerged index entries while keeping clean local mods.
    //
    // forceQuit switches to `git <op> --quit` (and, for ops without --quit, direct sentinel
    // removal). Use it as the second-attempt path when --abort can't restore HEAD because
    // the in-progress sentinel directory is malformed — e.g. a `.git/rebase-merge` left
    // over from a crashed rebase that's missing `head-name`, where --abort warns and
    // walks away without actually clearing the state.
    //
    // After running the command we re-probe GetOperationState under the same lock and treat
    // "exit 0 but state still detected" as a failure — git often prints a warning and exits
    // cleanly when it can't fully recover, and the user would otherwise see the dialog close
    // but the operation banner reappear immediately. ForceQuitAvailable is set in that case
    // so the dialog can offer the escape-hatch second click.
    public AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false)
        => RunLocked<AbortOutcome>(repo, () =>
        {
            // Pick the verb. For force-quit we prefer git's own --quit (it removes the
            // sequencer/rebase state without touching the index/workdir) and fall back to
            // direct sentinel removal for ops that don't have one.
            var args = forceQuit ? GetForceQuitArgs(state) : GetAbortArgs(state);
            string? cmdMsg = null;
            int? exitCode = null;
            if (args != null)
            {
                var result = _runner.Run(repo.Path, args);
                if (!result.Started) return new AbortOutcome.Failed("Failed to start git.");
                exitCode = result.ExitCode;
                cmdMsg = GitProcessRunner.CombineGitOutput(result.Stderr, result.Stdout);
            }
            else if (forceQuit)
            {
                // No `git X --quit` for this op (Merge, Bisect, UnmergedPaths) —
                // delete the sentinels ourselves. The user already confirmed they want
                // to abandon HEAD recovery, so this is the documented escape hatch.
                cmdMsg = ForceClearSentinels(repo, state);
                if (cmdMsg != null) return new AbortOutcome.Failed(cmdMsg);
            }
            else
            {
                return new AbortOutcome.Failed("Nothing to abort.");
            }

            // Authoritative check: does git still see an in-progress op? An exit-0
            // result combined with leftover state means the command warned and
            // walked away — typically a malformed sentinel dir from a prior crash.
            var stillStuck = GetOperationState(repo) != RepoOperationState.None;

            if (!stillStuck && (exitCode == null || exitCode == 0))
                return AbortOutcome.Ok;

            var msg = cmdMsg;
            if (string.IsNullOrEmpty(msg))
            {
                if (stillStuck && exitCode == 0)
                    msg = $"git {(args != null ? string.Join(' ', args) : "abort")} reported success but the {DescribeState(state)} state is still present.";
                else if (exitCode != null)
                    msg = $"git {(args != null ? string.Join(' ', args) : "abort")} exited with code {exitCode}.";
                else
                    msg = "Abort failed.";
            }

            // Offer force-quit only on the first attempt and only for ops where it
            // can do something useful. Force-quit's own failure shouldn't keep
            // re-offering it.
            var canForceQuit = !forceQuit && stillStuck && SupportsForceQuit(state);
            return new AbortOutcome.Failed(msg, ForceQuitAvailable: canForceQuit);
        }, static m => new AbortOutcome.Failed(m));

    private static string[]? GetAbortArgs(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => new[] { "merge", "--abort" },
        RepoOperationState.Rebase => new[] { "rebase", "--abort" },
        RepoOperationState.CherryPick => new[] { "cherry-pick", "--abort" },
        RepoOperationState.Revert => new[] { "revert", "--abort" },
        RepoOperationState.ApplyMailbox => new[] { "am", "--abort" },
        RepoOperationState.Bisect => new[] { "bisect", "reset" },
        RepoOperationState.UnmergedPaths => new[] { "reset", "--merge" },
        _ => null,
    };

    private static string[]? GetForceQuitArgs(RepoOperationState state) => state switch
    {
        RepoOperationState.Rebase => new[] { "rebase", "--quit" },
        RepoOperationState.CherryPick => new[] { "cherry-pick", "--quit" },
        RepoOperationState.Revert => new[] { "revert", "--quit" },
        RepoOperationState.ApplyMailbox => new[] { "am", "--quit" },
        // Merge, Bisect, UnmergedPaths have no native --quit — handled by sentinel removal.
        _ => null,
    };

    private static bool SupportsForceQuit(RepoOperationState state) => state switch
    {
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        RepoOperationState.Merge => true,
        // UnmergedPaths and Bisect: there's no sensible "give up restoring HEAD" since
        // there's no HEAD-restore phase to skip. `git reset --merge` and `git bisect reset`
        // either succeed or fail because of something the user has to address.
        _ => false,
    };

    // Last-resort cleanup for ops where git has no --quit verb. Each branch removes only
    // the sentinels that mark this specific op as in-progress; refs (HEAD, index, workdir)
    // are left alone. Returns null on success or a human-readable error string on failure.
    private string? ForceClearSentinels(Repo repo, RepoOperationState state)
    {
        try
        {
            var gitDir = GetGitDir(repo.Path);
            if (gitDir == null) return "Couldn't locate the repository's gitdir.";

            switch (state)
            {
                case RepoOperationState.Merge:
                    TryDeleteFile(Path.Combine(gitDir, "MERGE_HEAD"));
                    TryDeleteFile(Path.Combine(gitDir, "MERGE_MSG"));
                    TryDeleteFile(Path.Combine(gitDir, "MERGE_MODE"));
                    TryDeleteFile(Path.Combine(gitDir, "AUTO_MERGE"));
                    return null;
                case RepoOperationState.Rebase:
                    TryDeleteDir(Path.Combine(gitDir, "rebase-apply"));
                    TryDeleteDir(Path.Combine(gitDir, "rebase-merge"));
                    return null;
                case RepoOperationState.CherryPick:
                    TryDeleteFile(Path.Combine(gitDir, "CHERRY_PICK_HEAD"));
                    TryDeleteDir(Path.Combine(gitDir, "sequencer"));
                    return null;
                case RepoOperationState.Revert:
                    TryDeleteFile(Path.Combine(gitDir, "REVERT_HEAD"));
                    TryDeleteDir(Path.Combine(gitDir, "sequencer"));
                    return null;
                case RepoOperationState.ApplyMailbox:
                    TryDeleteDir(Path.Combine(gitDir, "rebase-apply"));
                    return null;
                default:
                    return $"Force-clear isn't supported for {DescribeState(state)}.";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string DescribeState(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => "merge",
        RepoOperationState.Rebase => "rebase",
        RepoOperationState.CherryPick => "cherry-pick",
        RepoOperationState.Revert => "revert",
        RepoOperationState.ApplyMailbox => "git-am",
        RepoOperationState.Bisect => "bisect",
        RepoOperationState.UnmergedPaths => "unmerged-paths",
        _ => "operation",
    };

    public ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state)
        => RunLocked<ContinueOutcome>(repo, () =>
        {
            var args = state switch
            {
                RepoOperationState.Merge => new[] { "merge", "--continue" },
                RepoOperationState.Rebase => new[] { "rebase", "--continue" },
                RepoOperationState.CherryPick => new[] { "cherry-pick", "--continue" },
                RepoOperationState.Revert => new[] { "revert", "--continue" },
                RepoOperationState.ApplyMailbox => new[] { "am", "--continue" },
                _ => null,
            };
            if (args == null)
                return new ContinueOutcome.Failed(
                    $"Continue isn't supported for {DescribeState(state)}.");
            return RunSequencerAdvance(repo, args);
        }, static m => new ContinueOutcome.Failed(m));

    public ContinueOutcome SkipOperation(Repo repo, RepoOperationState state)
        => RunLocked<ContinueOutcome>(repo, () =>
        {
            var args = state switch
            {
                RepoOperationState.Rebase => new[] { "rebase", "--skip" },
                RepoOperationState.CherryPick => new[] { "cherry-pick", "--skip" },
                RepoOperationState.Revert => new[] { "revert", "--skip" },
                RepoOperationState.ApplyMailbox => new[] { "am", "--skip" },
                _ => null,
            };
            if (args == null)
                return new ContinueOutcome.Failed(
                    $"Skip isn't supported for {DescribeState(state)}.");
            return RunSequencerAdvance(repo, args);
        }, static m => new ContinueOutcome.Failed(m));

    private ContinueOutcome RunSequencerAdvance(Repo repo, string[] args)
    {
        var result = _runner.Run(repo.Path, args, configure: static psi =>
        {
            psi.EnvironmentVariables["GIT_EDITOR"] = "true";
            psi.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";
        });
        if (result.Ok) return ContinueOutcome.Ok;

        bool hasMoreConflicts;
        try { hasMoreConflicts = HasUnmergedPaths(repo.Path); }
        catch { hasMoreConflicts = false; }

        var message = result.BlockError($"git {string.Join(' ', args)}");
        return hasMoreConflicts
            ? new ContinueOutcome.MoreConflicts(message)
            : new ContinueOutcome.Failed(message);
    }
}

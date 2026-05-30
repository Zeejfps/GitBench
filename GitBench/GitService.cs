using System.Collections.Concurrent;
using System.Diagnostics;
using LibGit2Sharp;

namespace GitGui;

public sealed class GitService : IGitService
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


    public CommitSnapshot Load(Repo repo, int cap)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return Error(repo, "Not a git repository.");

            using var lg = new Repository(repo.Path);

            var headTip = lg.Head?.Tip;
            var headSha = headTip?.Sha;

            var refTips = new List<Commit>();
            var refsBySha = new Dictionary<string, List<RefBadge>>();

            foreach (var branch in lg.Branches)
            {
                var tip = branch.Tip;
                if (tip == null) continue;
                refTips.Add(tip);
                var kind = branch.IsRemote ? RefKind.RemoteBranch : RefKind.LocalBranch;
                AddBadge(refsBySha, tip.Sha, new RefBadge(branch.FriendlyName, kind));
            }

            if (headSha != null)
                AddBadge(refsBySha, headSha, new RefBadge("HEAD", RefKind.Head));

            // Walk stash tips too so stash commits show in the graph. Stash entries are
            // merge commits whose parents include the index/untracked snapshots — those get
            // pulled in automatically via the topological walk.
            var stashIndex = 0;
            foreach (var stash in lg.Stashes)
            {
                var tip = stash.WorkTree;
                if (tip == null) { stashIndex++; continue; }
                refTips.Add(tip);
                var label = StripStashPrefix(stash.Message ?? string.Empty);
                if (string.IsNullOrEmpty(label)) label = $"stash@{{{stashIndex}}}";
                AddBadge(refsBySha, tip.Sha, new RefBadge(label, RefKind.Stash));
                stashIndex++;
            }

            if (refTips.Count == 0 && headTip != null)
                refTips.Add(headTip);

            var filter = new CommitFilter
            {
                IncludeReachableFrom = refTips,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            };

            var commitsRaw = new List<Commit>(cap);
            var truncated = false;
            foreach (var c in lg.Commits.QueryBy(filter))
            {
                if (commitsRaw.Count >= cap)
                {
                    truncated = true;
                    break;
                }
                commitsRaw.Add(c);
            }

            var inputs = new LaneAssigner.Input[commitsRaw.Count];
            for (var i = 0; i < commitsRaw.Count; i++)
            {
                var c = commitsRaw[i];
                var parentShas = c.Parents.Select(p => p.Sha).ToArray();
                inputs[i] = new LaneAssigner.Input(c.Sha, parentShas);
            }

            var (assignments, laneCount) = LaneAssigner.Assign(inputs);

            var nodes = new CommitNode[commitsRaw.Count];
            for (var i = 0; i < commitsRaw.Count; i++)
            {
                var c = commitsRaw[i];
                var a = assignments[i];
                var parentShas = (IReadOnlyList<string>)inputs[i].ParentShas;
                refsBySha.TryGetValue(c.Sha, out var badges);

                var inWalkParents = new ParentLink[a.InWalkParentLanes.Length];
                for (var k = 0; k < a.InWalkParentLanes.Length; k++)
                {
                    var p = a.InWalkParentLanes[k];
                    inWalkParents[k] = new ParentLink(p.ParentIndex, p.Lane);
                }

                nodes[i] = new CommitNode(
                    Sha: c.Sha,
                    Summary: c.MessageShort ?? string.Empty,
                    Author: c.Author?.Name ?? string.Empty,
                    When: c.Author?.When ?? c.Committer?.When ?? DateTimeOffset.MinValue,
                    ParentShas: parentShas,
                    Lane: a.Lane,
                    HasIncomingAtCommitLane: a.HasIncomingAtCommitLane,
                    InWalkParentLanes: inWalkParents,
                    IncomingLanes: a.IncomingLanes,
                    PassThroughLanes: a.PassThroughLanes,
                    Refs: badges ?? (IReadOnlyList<RefBadge>)Array.Empty<RefBadge>());
            }

            var headBranchName = lg.Info.IsHeadDetached ? null : lg.Head?.FriendlyName;
            return new CommitSnapshot(repo.Id, repo.Path, nodes, laneCount, truncated, null, headBranchName);
        }
        catch (Exception ex)
        {
            return Error(repo, ex.Message);
        }
    }

    private static CommitSnapshot Error(Repo repo, string message)
        => new(repo.Id, repo.Path, Array.Empty<CommitNode>(), 0, false, message);

    private static void AddBadge(Dictionary<string, List<RefBadge>> map, string sha, RefBadge badge)
    {
        if (!map.TryGetValue(sha, out var list))
        {
            list = new List<RefBadge>();
            map[sha] = list;
        }
        list.Add(badge);
    }

    public CommitDetails LoadDetails(Repo repo, string sha)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return DetailsError(repo, sha, "Not a git repository.");

            // One log call with NUL-separated fields. %B (raw message) is last so any
            // newlines inside it can't be confused with field boundaries. Split(_, 10)
            // caps the chunk count so a NUL inside the body (theoretical, not seen in
            // practice) lands in the body field rather than producing extra entries.
            const string fmt = "%H%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%P%x00%s%x00%B";
            var logOutput = RunGit(repo.Path, out var logErr, "log", "-1", $"--format={fmt}", sha);
            if (logOutput == null)
                return DetailsError(repo, sha, logErr ?? "Commit not found.");

            var parts = logOutput.Split('\0', 10);
            if (parts.Length < 10)
                return DetailsError(repo, sha, "Unexpected git log output.");

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
                Files: files,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return DetailsError(repo, sha, ex.Message);
        }
    }

    private static DateTimeOffset ParseIsoDateOrDefault(string s)
        => DateTimeOffset.TryParse(s, out var when) ? when : DateTimeOffset.MinValue;

    // Parses the NUL-separated output of `git diff-tree --name-status -z`. Each record is
    // "<status>\0<path>\0", except R/C records which carry a similarity score on the
    // status and a second path: "R100\0<old>\0<new>\0". Status letters map via the same
    // table as porcelain v2 (M/A/D/R/C/T).
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
    public BranchListing GetBranches(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new BranchListing(repo.Id, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), "Not a git repository.");

            // Seed with all configured remotes so groups still show even when a remote has
            // no branches yet (matches the prior LibGit2Sharp behavior).
            var remotesByName = new Dictionary<string, List<BranchEntry>>(StringComparer.Ordinal);
            var remotesOut = RunGit(repo.Path, out var remErr, "remote");
            if (remotesOut == null)
                return new BranchListing(repo.Id, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), remErr ?? "git remote failed.");
            foreach (var rawLine in remotesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = rawLine.Trim();
                if (name.Length > 0) remotesByName[name] = new List<BranchEntry>();
            }

            // Empty if HEAD is detached; we just compare for equality below, so null is fine.
            var headRef = RunGit(repo.Path, out _, "symbolic-ref", "-q", "HEAD")?.Trim();

            const char Sep = '\x1F';
            // %(upstream:track) collapses two distinct cases to "": (a) no upstream
            // configured at all, (b) upstream is set and we are exactly in sync. So we
            // also pull %(upstream) (the upstream ref name) to tell them apart.
            var fmt = $"%(objectname){Sep}%(refname){Sep}%(upstream:track,nobracket){Sep}%(upstream)";
            var branchesOut = RunGit(repo.Path, out var brErr,
                "for-each-ref", $"--format={fmt}", "refs/heads", "refs/remotes");
            if (branchesOut == null)
                return new BranchListing(repo.Id, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), brErr ?? "git for-each-ref failed.");

            var locals = new List<BranchEntry>();
            foreach (var line in branchesOut.Split('\n'))
            {
                if (line.Length == 0) continue;
                var parts = line.Split(Sep);
                if (parts.Length < 2) continue;
                var sha = parts[0];
                var refname = parts[1];
                var track = parts.Length > 2 ? parts[2] : string.Empty;
                var upstream = parts.Length > 3 ? parts[3] : string.Empty;

                if (refname.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    var name = refname["refs/heads/".Length..];
                    var isHead = headRef == refname;
                    var (ahead, behind, upstreamState) = ParseUpstream(track, upstream);
                    string? upstreamRemote = null;
                    string? upstreamBranch = null;
                    if (upstreamState == BranchUpstreamState.Tracked
                        && upstream.StartsWith("refs/remotes/", StringComparison.Ordinal))
                    {
                        var rest = upstream["refs/remotes/".Length..];
                        var slash = rest.IndexOf('/');
                        if (slash > 0)
                        {
                            upstreamRemote = rest[..slash];
                            upstreamBranch = rest[(slash + 1)..];
                        }
                    }
                    locals.Add(new BranchEntry(name, sha, isHead,
                        AheadBy: ahead, BehindBy: behind,
                        UpstreamState: upstreamState,
                        UpstreamRemote: upstreamRemote,
                        UpstreamBranch: upstreamBranch));
                }
                else if (refname.StartsWith("refs/remotes/", StringComparison.Ordinal))
                {
                    var rest = refname["refs/remotes/".Length..];
                    var slash = rest.IndexOf('/');
                    if (slash <= 0) continue;
                    var remoteName = rest[..slash];
                    var display = rest[(slash + 1)..];
                    // Skip the symbolic origin/HEAD ref; it just mirrors another branch.
                    if (display == "HEAD") continue;
                    if (!remotesByName.TryGetValue(remoteName, out var list))
                    {
                        list = new List<BranchEntry>();
                        remotesByName[remoteName] = list;
                    }
                    list.Add(new BranchEntry(display, sha, IsHead: false));
                }
            }

            locals.Sort((a, b) =>
            {
                if (a.IsHead != b.IsHead) return a.IsHead ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            var remoteGroups = new List<RemoteGroup>(remotesByName.Count);
            foreach (var kv in remotesByName.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                kv.Value.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                remoteGroups.Add(new RemoteGroup(kv.Key, kv.Value));
            }

            var stashes = LoadStashes(repo.Path);
            return new BranchListing(repo.Id, locals, remoteGroups, stashes, null);
        }
        catch (Exception ex)
        {
            return new BranchListing(repo.Id, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), ex.Message);
        }
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
    public LocalChangesSnapshot GetLocalChanges(Repo repo)
    {
        var tag = $"[Git status {repo.Path}]";
        var sw = Stopwatch.StartNew();
        try
        {
            if (!IsGitRepo(repo.Path))
                return LocalChangesError(repo, "Not a git repository.");

            var statusSw = Stopwatch.StartNew();
            var output = RunGitStatusPorcelain(repo.Path, out var error);
            statusSw.Stop();
            Console.WriteLine($"{tag} git status returned in {statusSw.ElapsedMilliseconds}ms ({output?.Length ?? 0} bytes)");
            if (output == null)
                return LocalChangesError(repo, error ?? "git status failed.");

            var staged = new List<FileChange>();
            var unstaged = new List<FileChange>();

            // Porcelain v2 with -z is NUL-terminated. Most records are a single
            // NUL-terminated line; type-2 (rename/copy) records carry an additional
            // NUL-terminated origPath right after, so we walk byte-by-byte rather than
            // splitting on NUL up front.
            var idx = 0;
            while (idx < output.Length)
            {
                var end = output.IndexOf('\0', idx);
                if (end < 0) break;
                var record = output[idx..end];
                idx = end + 1;
                if (record.Length == 0) continue;

                var kind = record[0];

                if (kind == '?')
                {
                    // "? path" — untracked.
                    var path = record.Length > 2 ? record[2..] : string.Empty;
                    if (path.Length > 0)
                        unstaged.Add(new FileChange(path, null, FileChangeStatus.Added));
                    continue;
                }

                if (kind == '!')
                {
                    // Ignored — not requested, but skip defensively if it ever appears.
                    continue;
                }

                if (kind == 'u')
                {
                    // "u XY sub m1 m2 m3 mW h1 h2 h3 path" — unmerged. Surface in unstaged
                    // only; the user has to resolve and stage to clear it. Splitting into
                    // both panels would invite a half-staged conflict resolution.
                    var parts = record.Split(' ', 11);
                    if (parts.Length < 11) continue;
                    unstaged.Add(new FileChange(parts[10], null, FileChangeStatus.Conflicted));
                    continue;
                }

                if (kind == '1')
                {
                    // "1 XY sub mH mI mW hH hI path"
                    var parts = record.Split(' ', 9);
                    if (parts.Length < 9) continue;
                    var xy = parts[1];
                    if (xy.Length < 2) continue;
                    AddIndexAndWorkdirEntries(staged, unstaged, xy[0], xy[1], parts[8], origPath: null);
                    continue;
                }

                if (kind == '2')
                {
                    // "2 XY sub mH mI mW hH hI Xscore path"  then  "origPath"
                    var parts = record.Split(' ', 10);
                    if (parts.Length < 10) continue;
                    var xy = parts[1];
                    if (xy.Length < 2) continue;
                    var origEnd = output.IndexOf('\0', idx);
                    if (origEnd < 0) continue;
                    var origPath = output[idx..origEnd];
                    idx = origEnd + 1;
                    AddIndexAndWorkdirEntries(staged, unstaged, xy[0], xy[1], parts[9], origPath);
                    continue;
                }
            }

            staged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            unstaged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            sw.Stop();
            Console.WriteLine($"{tag} done in {sw.ElapsedMilliseconds}ms (staged={staged.Count}, unstaged={unstaged.Count})");
            return new LocalChangesSnapshot(repo.Id, staged, unstaged, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"{tag} threw in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return LocalChangesError(repo, ex.Message);
        }
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
    private string? RunGitStatusPorcelain(string workingDir, out string? error)
    {
        error = null;
        var result = _runner.Run(
            workingDir,
            new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored=no" },
            GitProcessRunner.GitLaunch.Direct);
        if (result.Ok) return result.Stdout;
        error = result.FirstLineError("git status");
        return null;
    }

    private static LocalChangesSnapshot LocalChangesError(Repo repo, string message)
        => new(repo.Id, Array.Empty<FileChange>(), Array.Empty<FileChange>(), message);

    public void Stage(Repo repo, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        using var _ = LockRepo(repo.Path);
        var args = new List<string>(paths.Count + 2) { "add", "--" };
        args.AddRange(paths);
        RunGitMutationOrThrow(repo.Path, args);
    }

    public void Unstage(Repo repo, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        using var _ = LockRepo(repo.Path);
        var args = new List<string>(paths.Count + 3) { "restore", "--staged", "--" };
        args.AddRange(paths);
        RunGitMutationOrThrow(repo.Path, args);
    }

    public string? ApplyPatch(Repo repo, string patch, bool cached, bool reverse)
    {
        if (string.IsNullOrEmpty(patch)) return null;
        try
        {
            if (!IsGitRepo(repo.Path)) return "Not a git repository.";

            using var _ = LockRepo(repo.Path);
            var args = new List<string> { "apply", "--whitespace=nowarn" };
            if (cached) args.Add("--cached");
            if (reverse) args.Add("--reverse");
            args.Add("-");
            return RunGitWithStdin(repo.Path, args, patch);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private string? RunGitWithStdin(string workingDir, IReadOnlyList<string> args, string stdin)
    {
        var result = _runner.Run(workingDir, args, GitProcessRunner.GitLaunch.Direct, stdin);
        return result.Ok ? null : result.BlockError("git apply");
    }

    private void RunGitMutationOrThrow(string repoPath, IReadOnlyList<string> args)
    {
        var (ok, error) = RunMutation(repoPath, args);
        if (!ok) throw new InvalidOperationException(error ?? "git command failed.");
    }

    public void ResetToParent(Repo repo, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        using var _lock = LockRepo(repo.Path);
        // No HEAD (unborn branch) → nothing to reset to. Root commit (HEAD with no
        // parent) → `git reset` has nothing to copy from, so drop the entries from
        // the index without touching the workdir. Otherwise let `git reset HEAD^`
        // copy parent blobs back into the index (or remove entries the parent didn't
        // have). The working tree is untouched in all paths.
        if (RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD") == null)
            return;
        var hasParent = RunGit(repo.Path, out _, "rev-parse", "--verify", "-q", "HEAD^") != null;
        var args = hasParent
            ? new List<string>(paths.Count + 3) { "reset", "HEAD^", "--" }
            : new List<string>(paths.Count + 4) { "rm", "--cached", "--force", "--" };
        args.AddRange(paths);
        RunGitMutationOrThrow(repo.Path, args);
    }

    // Throws away unstaged workdir changes for the given paths. Tracked files are restored
    // from the index via `git checkout -- <paths>` (the user's staged hunks are preserved);
    // untracked files (not in the index) are deleted from disk. Returns null on success or
    // a human-readable error string on failure.
    public string? DiscardChanges(Repo repo, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        try
        {
            if (!IsGitRepo(repo.Path))
                return "Not a git repository.";

            using var _ = LockRepo(repo.Path);
            // `git ls-files -z -- <paths>` prints only the tracked subset, NUL-separated.
            // Anything not in that subset exists only on disk and gets deleted directly;
            // tracked entries fall through to the `git checkout --` restore below.
            var lsArgs = new string[paths.Count + 3];
            lsArgs[0] = "ls-files";
            lsArgs[1] = "-z";
            lsArgs[2] = "--";
            for (var i = 0; i < paths.Count; i++) lsArgs[i + 3] = paths[i];
            var lsOutput = RunGit(repo.Path, out var lsErr, lsArgs);
            if (lsOutput == null) return lsErr ?? "git ls-files failed.";
            var tracked = new HashSet<string>(
                lsOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

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
                    return ex.Message;
                }
            }

            if (trackedPaths.Count > 0)
            {
                var args = new List<string> { "checkout", "--" };
                args.AddRange(trackedPaths);
                var result = _runner.Run(repo.Path, args);
                if (!result.Ok) return result.FirstLineError("git checkout");
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string? Commit(Repo repo, string message, bool amend)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return "Not a git repository.";

            var args = new List<string> { "commit", "-m", message };
            if (amend) args.Add("--amend");

            using var _ = LockRepo(repo.Path);
            // -m supplies the message, but a configured core.editor would still fire for
            // merge/rebase/squash flows that prompt to confirm the commit message.
            var result = _runner.Run(repo.Path, args,
                configure: static psi => psi.EnvironmentVariables["GIT_EDITOR"] = "true");
            return result.Ok ? null : result.BlockError("git commit");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

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

    public IReadOnlyList<FileChange> GetHeadCommitFiles(Repo repo)
    {
        var tag = $"[Git head-files {repo.Path}]";
        var sw = Stopwatch.StartNew();
        try
        {
            if (!IsGitRepo(repo.Path)) return Array.Empty<FileChange>();
            var output = RunGit(repo.Path, out _, "diff-tree", "-r", "-M", "--name-status",
                "--no-commit-id", "-z", "--root", "HEAD");
            if (output == null) return Array.Empty<FileChange>();
            var files = ParseDiffTreeNameStatusZ(output);
            files.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            Console.WriteLine($"{tag} done in {sw.ElapsedMilliseconds}ms ({files.Count} files)");
            return files;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"{tag} threw in {sw.ElapsedMilliseconds}ms: {ex.Message}");
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

    // Shells out to the `git` CLI so we inherit the user's credential helpers
    // (ssh-agent, osxkeychain, GitHub CLI, …) — libgit2's macOS SSH path is too brittle.
    //
    // force=true uses --force-with-lease: refuses if the remote moved since our last fetch,
    // so a teammate's concurrent push isn't silently clobbered. Caller is expected to have
    // confirmed with the user before passing force=true.
    public PushOutcome Push(Repo repo, bool force = false)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new PushOutcome(false, "Not a git repository.");

            using var _ = LockRepo(repo.Path);
            // Pre-flight: refuse to push from detached HEAD or a branch with no upstream,
            // because the resulting `git push` error is less actionable than these messages.
            var info = GetHeadInfo(repo.Path);
            if (info.IsDetached)
                return new PushOutcome(false, "HEAD is detached. Check out a branch first.");
            if (!info.HasUpstream)
            {
                var name = info.CurrentBranchName ?? "(unknown)";
                return new PushOutcome(false,
                    $"Branch '{name}' has no upstream. Set one with: git push -u <remote> {name}");
            }

            var args = new List<string> { "push" };
            if (force) args.Add("--force-with-lease");
            var result = _runner.Run(repo.Path, args);
            return result.Ok ? new PushOutcome(true, null) : new PushOutcome(false, result.FirstLineError("git push"));
        }
        catch (Exception ex)
        {
            return new PushOutcome(false, ex.Message);
        }
    }

    public PushOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new PushOutcome(false, "Not a git repository.");
            if (string.IsNullOrWhiteSpace(localBranch))
                return new PushOutcome(false, "Local branch is required.");
            if (string.IsNullOrWhiteSpace(remoteName))
                return new PushOutcome(false, "Remote is required.");
            if (string.IsNullOrWhiteSpace(remoteBranchName))
                return new PushOutcome(false, "Remote branch name is required.");

            var args = new List<string> { "push" };
            if (setUpstream) args.Add("--set-upstream");
            args.Add(remoteName);
            args.Add($"{localBranch}:{remoteBranchName}");

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            return result.Ok ? new PushOutcome(true, null) : new PushOutcome(false, result.FirstLineError("git push"));
        }
        catch (Exception ex)
        {
            return new PushOutcome(false, ex.Message);
        }
    }

    public IReadOnlyList<string> GetRemoteNames(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path)) return Array.Empty<string>();
            var output = RunGit(repo.Path, out _, "remote");
            if (string.IsNullOrEmpty(output)) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (name.Length > 0) list.Add(name);
            }
            return list;
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
            var output = RunGit(repo.Path, out var error, "remote", "get-url", remoteName);
            if (error != null) return null;
            var url = output.Trim();
            return url.Length == 0 ? null : url;
        }
        catch
        {
            return null;
        }
    }

    public EditRemoteOutcome EditRemote(Repo repo, string oldName, string newName, string url)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new EditRemoteOutcome(false, "Not a git repository.");

            using var _ = LockRepo(repo.Path);
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                var (renamed, renameError) = RunMutation(repo.Path, new[] { "remote", "rename", oldName, newName });
                if (!renamed) return new EditRemoteOutcome(false, renameError ?? "Failed to rename remote.");
            }

            var (urlSet, urlError) = RunMutation(repo.Path, new[] { "remote", "set-url", newName, url });
            if (!urlSet) return new EditRemoteOutcome(false, urlError ?? "Failed to set remote URL.");

            return new EditRemoteOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new EditRemoteOutcome(false, ex.Message);
        }
    }

    public PullOutcome Pull(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new PullOutcome(false, "Not a git repository.");

            using var _ = LockRepo(repo.Path);
            var info = GetHeadInfo(repo.Path);
            if (info.IsDetached)
                return new PullOutcome(false, "HEAD is detached. Check out a branch first.");
            if (!info.HasUpstream)
            {
                var name = info.CurrentBranchName ?? "(unknown)";
                return new PullOutcome(false,
                    $"Branch '{name}' has no upstream. Set one with: git branch --set-upstream-to=<remote>/<branch>");
            }

            var result = _runner.Run(repo.Path, new[] { "pull" });
            return result.Ok ? new PullOutcome(true, null) : new PullOutcome(false, result.FirstLineError("git pull"));
        }
        catch (Exception ex)
        {
            return new PullOutcome(false, ex.Message);
        }
    }

    public FetchOutcome Fetch(Repo repo)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new FetchOutcome(false, "Not a git repository.");

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, new[] { "fetch", "--all", "--prune" });
            return result.Ok ? new FetchOutcome(true, null) : new FetchOutcome(false, result.FirstLineError("git fetch"));
        }
        catch (Exception ex)
        {
            return new FetchOutcome(false, ex.Message);
        }
    }

    public FastForwardOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null)
    {
        var tag = $"[Git ff {localBranch} <- {remoteName}/{remoteBranch}]";
        try
        {
            if (!IsGitRepo(repo.Path))
                return new FastForwardOutcome(false, "Not a git repository.");

            var refspec = $"{remoteBranch}:{localBranch}";
            var args = new List<string> { "fetch", "--progress", remoteName, refspec };
            using var _ = LockRepo(repo.Path);
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"{tag} starting");

            var (exitCode, captureText, started) = _runner.RunStreaming(repo.Path, args, line =>
            {
                Console.WriteLine($"{tag}   {line}");
                onLine?.Invoke(line);
            });
            sw.Stop();

            if (!started) return new FastForwardOutcome(false, "Failed to start git.");

            if (exitCode == 0)
            {
                Console.WriteLine($"{tag} done in {sw.ElapsedMilliseconds}ms");
                return new FastForwardOutcome(true, null);
            }

            var msg = GitProcessRunner.FirstMeaningfulLine(captureText);
            if (string.IsNullOrEmpty(msg)) msg = $"git fetch exited with code {exitCode}.";
            msg = GitProcessRunner.AugmentCredentialError(msg, captureText);
            Console.WriteLine($"{tag} failed in {sw.ElapsedMilliseconds}ms: {msg}");
            return new FastForwardOutcome(false, msg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{tag} threw: {ex.Message}");
            return new FastForwardOutcome(false, ex.Message);
        }
    }

    // Shells out so post-checkout hooks, LFS, and sparse-checkout filters all run; also
    // surfaces the same error wording the user would see in Terminal.
    public CheckoutOutcome CheckoutLocalBranch(Repo repo, string branchName)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new CheckoutOutcome(false, "Not a git repository.");

            using var _ = LockRepo(repo.Path);
            return RunGitCheckout(repo.Path, new[] { "checkout", branchName });
        }
        catch (Exception ex)
        {
            return new CheckoutOutcome(false, ex.Message);
        }
    }

    public ResetOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new ResetOutcome(false, "Not a git repository.");

            var flag = mode switch
            {
                ResetMode.Soft => "--soft",
                ResetMode.Mixed => "--mixed",
                ResetMode.Hard => "--hard",
                _ => "--mixed",
            };

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, new[] { "reset", flag, commitSha });
            return result.Ok ? new ResetOutcome(true, null) : new ResetOutcome(false, result.FirstLineError("git reset"));
        }
        catch (Exception ex)
        {
            return new ResetOutcome(false, ex.Message);
        }
    }

    public CheckoutOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new CheckoutOutcome(false, "Not a git repository.");

            var args = new List<string>
            {
                "checkout", "-b", localName,
                track ? "--track" : "--no-track",
                $"{remoteName}/{remoteBranchName}",
            };
            using var _ = LockRepo(repo.Path);
            return RunGitCheckout(repo.Path, args);
        }
        catch (Exception ex)
        {
            return new CheckoutOutcome(false, ex.Message);
        }
    }

    // Shells out so post-checkout hooks run when `checkout` is true, and the error wording
    // matches the user's terminal experience (e.g. "fatal: A branch named 'x' already exists.").
    public CreateBranchOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new CreateBranchOutcome(false, "Not a git repository.");

            var args = checkout
                ? new List<string> { "checkout", "-b", name, startPoint }
                : new List<string> { "branch", name, startPoint };

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            return result.Ok
                ? new CreateBranchOutcome(true, null)
                : new CreateBranchOutcome(false, result.FirstLineError($"git {(checkout ? "checkout" : "branch")}"));
        }
        catch (Exception ex)
        {
            return new CreateBranchOutcome(false, ex.Message);
        }
    }

    // `git branch -m` (or -M with force) renames a local branch in-place. Allowed on the
    // currently-checked-out branch — git updates HEAD's symbolic ref to point at the new name.
    public RenameBranchOutcome RenameBranch(Repo repo, string oldName, string newName, bool force)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new RenameBranchOutcome(false, "Not a git repository.");

            var args = new List<string> { "branch", force ? "-M" : "-m", oldName, newName };
            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            return result.Ok ? new RenameBranchOutcome(true, null) : new RenameBranchOutcome(false, result.BlockError("git branch"));
        }
        catch (Exception ex)
        {
            return new RenameBranchOutcome(false, ex.Message);
        }
    }

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
    public MergeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new MergeOutcome(false, "Not a git repository.");

            var args = new List<string> { "merge" };
            switch (strategy)
            {
                case MergeStrategy.NoFastForward: args.Add("--no-ff"); break;
                case MergeStrategy.FastForwardOnly: args.Add("--ff-only"); break;
                case MergeStrategy.Squash: args.Add("--squash"); break;
            }
            args.Add(sourceRef);

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            if (result.Ok) return new MergeOutcome(true, null);

            // Conflict path: MERGE_HEAD exists in the per-worktree gitdir.
            // --squash and --ff-only never create MERGE_HEAD, so failures there are
            // always real errors.
            if (strategy != MergeStrategy.Squash && strategy != MergeStrategy.FastForwardOnly)
            {
                try
                {
                    var gitDir = GetGitDir(repo.Path);
                    if (gitDir != null && File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                        return new MergeOutcome(true, null, HasConflicts: true);
                }
                catch { /* fall through to error */ }
            }

            return new MergeOutcome(false, result.BlockError("git merge"));
        }
        catch (Exception ex)
        {
            return new MergeOutcome(false, ex.Message);
        }
    }

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
    public RebaseOutcome Rebase(Repo repo, string targetRef, bool autostash)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new RebaseOutcome(false, "Not a git repository.");

            var args = new List<string> { "rebase" };
            if (autostash) args.Add("--autostash");
            args.Add(targetRef);

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            if (result.Ok) return new RebaseOutcome(true, null);

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
                    return new RebaseOutcome(true, null, HasConflicts: true);
                }
            }
            catch { /* fall through to error */ }

            return new RebaseOutcome(false, result.BlockError("git rebase"));
        }
        catch (Exception ex)
        {
            return new RebaseOutcome(false, ex.Message);
        }
    }

    // `git branch -d` refuses to delete a branch not fully merged into its upstream/HEAD;
    // `-D` force-deletes regardless. Also refuses to delete the currently-checked-out branch
    // — callers should gate that in the UI rather than relying on the error.
    public DeleteBranchOutcome DeleteBranch(Repo repo, string name, bool force)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new DeleteBranchOutcome(false, "Not a git repository.");

            var args = new List<string> { "branch", force ? "-D" : "-d", name };
            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            return result.Ok ? new DeleteBranchOutcome(true, null) : new DeleteBranchOutcome(false, result.BlockError("git branch"));
        }
        catch (Exception ex)
        {
            return new DeleteBranchOutcome(false, ex.Message);
        }
    }

    // Shells out to `git push <remote> --delete <branch>`. The local copy is unaffected.
    // Server may refuse for protected refs — we surface whatever git reports.
    public DeleteRemoteBranchOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new DeleteRemoteBranchOutcome(false, "Not a git repository.");

            var args = new List<string> { "push", remoteName, "--delete", branchName };
            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args);
            return result.Ok ? new DeleteRemoteBranchOutcome(true, null) : new DeleteRemoteBranchOutcome(false, result.BlockError("git push"));
        }
        catch (Exception ex)
        {
            return new DeleteRemoteBranchOutcome(false, ex.Message);
        }
    }

    public StashOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new StashOutcome(false, "Not a git repository.");

            var args = new List<string> { "stash", "push" };
            if (includeUntracked) args.Add("--include-untracked");
            if (keepIndex) args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                args.Add("-m");
                args.Add(message);
            }
            if (paths.Count > 0)
            {
                args.Add("--");
                foreach (var p in paths) args.Add(p);
            }

            using var _ = LockRepo(repo.Path);
            return RunGitStash(repo.Path, args, "git stash push");
        }
        catch (Exception ex)
        {
            return new StashOutcome(false, ex.Message);
        }
    }

    public StashOutcome ApplyStash(Repo repo, int index)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new StashOutcome(false, "Not a git repository.");

            var args = new List<string> { "stash", "apply", $"stash@{{{index}}}" };
            using var _ = LockRepo(repo.Path);
            // Snapshot the pre-apply index state. The "apply succeeded with conflicts"
            // heuristic below relies on the transition from clean → unmerged to decide
            // whether the non-zero exit is benign — if the index was already unmerged
            // (e.g. from an earlier failed apply the user hasn't cleared), the post-
            // apply check can't distinguish "this apply produced conflicts" from
            // "those leftover conflicts are still there" and we'd silently swallow
            // the real failure ("untracked file would be overwritten", etc).
            var wasFullyMerged = !HasUnmergedPaths(repo.Path);

            var outcome = RunGitStash(repo.Path, args, "git stash apply");
            if (outcome.Success) return outcome;

            // `git stash apply` exits 1 when the apply itself worked but produced
            // merge conflicts — the user's stash is on disk, the conflicts are visible
            // in the index, and there's nothing to "fix" about the apply itself. Treat
            // that as success-with-conflicts so the caller can refresh and show the
            // banner instead of an error dialog. Gate on wasFullyMerged so a real
            // failure on a repo that already had conflicts still surfaces its error.
            if (wasFullyMerged && HasUnmergedPaths(repo.Path))
                return new StashOutcome(true, null, HasConflicts: true);
            return outcome;
        }
        catch (Exception ex)
        {
            return new StashOutcome(false, ex.Message);
        }
    }

    public StashOutcome DropStash(Repo repo, int index)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new StashOutcome(false, "Not a git repository.");

            var args = new List<string> { "stash", "drop", $"stash@{{{index}}}" };
            using var _ = LockRepo(repo.Path);
            return RunGitStash(repo.Path, args, "git stash drop");
        }
        catch (Exception ex)
        {
            return new StashOutcome(false, ex.Message);
        }
    }

    public StashOutcome RenameStash(Repo repo, int index, string newMessage)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new StashOutcome(false, "Not a git repository.");

            using var _lock = LockRepo(repo.Path);

            // git has no native stash rename. Resolve the stash commit, drop the entry,
            // then re-store it under the new message. `git stash store` pushes the entry
            // back onto refs/stash, so a renamed stash moves to the top (stash@{0}).
            var sha = RunGit(repo.Path, out _, "rev-parse", $"stash@{{{index}}}")?.Trim();
            if (string.IsNullOrEmpty(sha))
                return new StashOutcome(false, "Could not resolve stash commit.");

            var dropOutcome = RunGitStash(repo.Path, new[] { "stash", "drop", $"stash@{{{index}}}" }, "git stash drop");
            if (!dropOutcome.Success) return dropOutcome;

            return RunGitStash(repo.Path, new[] { "stash", "store", "-m", newMessage, sha }, "git stash store");
        }
        catch (Exception ex)
        {
            return new StashOutcome(false, ex.Message);
        }
    }

    public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            if (!IsGitRepo(primary.Path))
            {
                errorMessage = "Not a git repository.";
                return Array.Empty<WorktreeInfo>();
            }
            var stdout = RunGit(primary.Path, out var err, "worktree", "list", "--porcelain");
            if (err != null)
            {
                errorMessage = err;
                return Array.Empty<WorktreeInfo>();
            }
            return ParseWorktreePorcelain(stdout);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
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

    public WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new WorktreeAddOutcome(false, "Not a git repository.");
            if (string.IsNullOrWhiteSpace(request.Path))
                return new WorktreeAddOutcome(false, "Worktree path is required.");
            if (string.IsNullOrWhiteSpace(request.StartPoint))
                return new WorktreeAddOutcome(false, "Start point is required.");

            var args = new List<string> { "worktree", "add" };
            if (request.Force) args.Add("--force");
            if (!string.IsNullOrWhiteSpace(request.NewBranchName))
            {
                args.Add("-b");
                args.Add(request.NewBranchName!);
            }
            args.Add(request.Path);
            args.Add(request.StartPoint);

            using var _ = LockRepo(primary.Path);
            return RunWorktreeAdd(primary.Path, args);
        }
        catch (Exception ex)
        {
            return new WorktreeAddOutcome(false, ex.Message);
        }
    }

    private WorktreeAddOutcome RunWorktreeAdd(string repoPath, IReadOnlyList<string> args)
    {
        var result = _runner.Run(repoPath, args);
        return result.Ok ? new WorktreeAddOutcome(true, null) : new WorktreeAddOutcome(false, result.BlockError("git worktree add"));
    }

    public WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new WorktreeRemoveOutcome(false, "Not a git repository.");
            if (string.IsNullOrWhiteSpace(worktreePath))
                return new WorktreeRemoveOutcome(false, "Worktree path is required.");

            var args = new List<string> { "worktree", "remove" };
            if (force) args.Add("--force");
            args.Add(worktreePath);

            using var _ = LockRepo(primary.Path);
            var result = _runner.Run(primary.Path, args);
            return result.Ok ? new WorktreeRemoveOutcome(true, null) : new WorktreeRemoveOutcome(false, result.BlockError("git worktree remove"));
        }
        catch (Exception ex)
        {
            return new WorktreeRemoveOutcome(false, ex.Message);
        }
    }

    public WorktreePruneOutcome PruneWorktrees(Repo primary)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new WorktreePruneOutcome(false, "Not a git repository.");

            using var _ = LockRepo(primary.Path);
            var result = _runner.Run(primary.Path, new[] { "worktree", "prune" });
            return result.Ok ? new WorktreePruneOutcome(true, null) : new WorktreePruneOutcome(false, result.BlockError("git worktree prune"));
        }
        catch (Exception ex)
        {
            return new WorktreePruneOutcome(false, ex.Message);
        }
    }

    // ────────── submodules ──────────

    public IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary, out string? errorMessage)
    {
        errorMessage = null;
        var tag = $"[Git submodules {primary.Path}]";
        var sw = Stopwatch.StartNew();
        try
        {
            if (!IsGitRepo(primary.Path))
            {
                errorMessage = "Not a git repository.";
                return Array.Empty<SubmoduleInfo>();
            }

            var gitmodulesPath = System.IO.Path.Combine(primary.Path, ".gitmodules");
            if (!File.Exists(gitmodulesPath))
            {
                Console.WriteLine($"{tag} no .gitmodules ({sw.ElapsedMilliseconds}ms)");
                return Array.Empty<SubmoduleInfo>();
            }

            // Step 1: enumerate logical entries from .gitmodules. Each `submodule.<name>.path`
            // row gives us one submodule; .url and .branch hang off the same <name>.
            var stepSw = Stopwatch.StartNew();
            var configOut = RunGit(primary.Path, out var cfgErr, "config", "--file", ".gitmodules", "--list");
            stepSw.Stop();
            Console.WriteLine($"{tag} config --list returned in {stepSw.ElapsedMilliseconds}ms ({configOut?.Length ?? 0} bytes)");
            if (cfgErr != null)
            {
                errorMessage = cfgErr;
                return Array.Empty<SubmoduleInfo>();
            }

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

            // Step 2: per-path status + describe + current SHA from `git submodule status`.
            // Per line: '<flag><sha> <path> (<describe>)'
            //   flag ' ' = up-to-date, '+' = modified, '-' = not initialized, 'U' = conflict.
            stepSw.Restart();
            var statusOut = RunGit(primary.Path, out _, "submodule", "status");
            stepSw.Stop();
            Console.WriteLine($"{tag} submodule status returned in {stepSw.ElapsedMilliseconds}ms ({statusOut?.Length ?? 0} bytes)");
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

            // Step 3: authoritative recorded SHA via `git ls-tree HEAD` — submodule status's
            // SHA reports the CURRENT checkout (or a leading + when modified), not what the
            // parent's HEAD tree actually records. ls-tree gives the recorded pointer directly.
            stepSw.Restart();
            var lsTreeOut = RunGit(primary.Path, out _, "ls-tree", "-r", "HEAD");
            stepSw.Stop();
            Console.WriteLine($"{tag} ls-tree -r HEAD returned in {stepSw.ElapsedMilliseconds}ms ({lsTreeOut?.Length ?? 0} bytes)");
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
            sw.Stop();
            Console.WriteLine($"{tag} done in {sw.ElapsedMilliseconds}ms ({results.Count} submodules)");
            return results;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"{tag} threw in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            errorMessage = ex.Message;
            return Array.Empty<SubmoduleInfo>();
        }
    }

    public SubmoduleAddOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new SubmoduleAddOutcome(false, "Not a git repository.");
            if (string.IsNullOrWhiteSpace(request.Url))
                return new SubmoduleAddOutcome(false, "Submodule URL is required.");
            if (string.IsNullOrWhiteSpace(request.Path))
                return new SubmoduleAddOutcome(false, "Submodule path is required.");

            var args = new List<string> { "submodule", "add" };
            if (request.Force) args.Add("--force");
            if (!string.IsNullOrWhiteSpace(request.Branch))
            {
                args.Add("-b");
                args.Add(request.Branch!);
            }
            args.Add(request.Url);
            args.Add(request.Path);

            using var _ = LockRepo(primary.Path);
            var (ok, err) = RunMutation(primary.Path, args);
            return new SubmoduleAddOutcome(ok, err);
        }
        catch (Exception ex)
        {
            return new SubmoduleAddOutcome(false, ex.Message);
        }
    }

    public SubmoduleUpdateOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new SubmoduleUpdateOutcome(false, "Not a git repository.");

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

            using var _ = LockRepo(primary.Path);
            var result = _runner.Run(primary.Path, args);
            if (result.Ok) return new SubmoduleUpdateOutcome(true, null);
            // Merge/rebase strategies surface CONFLICT markers in stdout when they fail —
            // hand that signal up so the dialog can show a "see Operation banner" hint
            // instead of just a raw error.
            var combined = result.Stdout + "\n" + result.Stderr;
            var conflicts = combined.Contains("CONFLICT", StringComparison.Ordinal)
                            || combined.Contains("merge conflict", StringComparison.OrdinalIgnoreCase);
            return new SubmoduleUpdateOutcome(false, result.BlockError("git submodule update"), conflicts);
        }
        catch (Exception ex)
        {
            return new SubmoduleUpdateOutcome(false, ex.Message);
        }
    }

    public SubmoduleDeinitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force)
    {
        try
        {
            if (!IsGitRepo(primary.Path))
                return new SubmoduleDeinitOutcome(false, "Not a git repository.");
            if (string.IsNullOrWhiteSpace(submodulePath))
                return new SubmoduleDeinitOutcome(false, "Submodule path is required.");

            using var _ = LockRepo(primary.Path);
            // Two-step: deinit frees the working tree + .git/modules entry; rm removes
            // the gitlink and the .gitmodules entry, staging the change as a commit-ready
            // deletion. Both happen under the same lock so the user sees one atomic op.
            var deinitArgs = new List<string> { "submodule", "deinit" };
            if (force) deinitArgs.Add("--force");
            deinitArgs.Add("--");
            deinitArgs.Add(submodulePath);
            var (ok1, err1) = RunMutation(primary.Path, deinitArgs);
            if (!ok1) return new SubmoduleDeinitOutcome(false, err1);

            var rmArgs = new List<string> { "rm" };
            if (force) rmArgs.Add("-f");
            rmArgs.Add("--");
            rmArgs.Add(submodulePath);
            var (ok2, err2) = RunMutation(primary.Path, rmArgs);
            if (!ok2) return new SubmoduleDeinitOutcome(false, err2);

            return new SubmoduleDeinitOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new SubmoduleDeinitOutcome(false, ex.Message);
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
                if (!raw.StartsWith(":", StringComparison.Ordinal)) continue;
                var tab = raw.IndexOf('\t');
                if (tab < 0) continue;
                var meta = raw.Substring(1, tab - 1);
                var pathPart = raw.Substring(tab + 1);
                var parts = meta.Split(' ');
                if (parts.Length < 5) continue;
                var srcMode = parts[0];
                var dstMode = parts[1];
                // Only gitlink entries (160000 on either side).
                if (srcMode != "160000" && dstMode != "160000") continue;
                var srcSha = parts[2];
                var dstSha = parts[3];

                int ahead = 0, behind = 0;
                string? shortLog = null;
                var subPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(repo.Path, pathPart));
                var srcZero = IsAllZeros(srcSha);
                var dstZero = IsAllZeros(dstSha);
                // Only resolve range info when the submodule is initialized locally AND both
                // ends are real commits — added/removed entries have a 40-zero sentinel on
                // one side that no rev-list query can resolve.
                if (!srcZero && !dstZero && Directory.Exists(subPath) && IsGitRepo(subPath))
                {
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

                results.Add(new SubmodulePointerChange(
                    Path: NormalizeRelPath(pathPart),
                    FromSha: srcSha,
                    ToSha: dstSha,
                    AheadCount: ahead,
                    BehindCount: behind,
                    ShortLog: shortLog));
            }
            return results;
        }
        catch
        {
            return Array.Empty<SubmodulePointerChange>();
        }
    }

    // Small shared helper for "spawn git, return (ok, errorOrNull)". Used where multiple
    // successive mutations need to be sequenced inside a single repo lock.
    private (bool Ok, string? Error) RunMutation(string repoPath, IReadOnlyList<string> args)
    {
        var result = _runner.Run(repoPath, args);
        return result.Ok ? (true, null) : (false, result.BlockError($"git {string.Join(' ', args)}"));
    }

    private static string NormalizeRelPath(string p) => p.Replace('\\', '/').TrimEnd('/');

    private static bool IsAllZeros(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] != '0') return false;
        return s.Length > 0;
    }

    private StashOutcome RunGitStash(string repoPath, IReadOnlyList<string> gitArgs, string label)
    {
        var result = _runner.Run(repoPath, gitArgs);
        return result.Ok ? new StashOutcome(true, null) : new StashOutcome(false, result.BlockError(label));
    }

    private CheckoutOutcome RunGitCheckout(string repoPath, IReadOnlyList<string> gitArgs)
    {
        var result = _runner.Run(repoPath, gitArgs);
        return result.Ok ? new CheckoutOutcome(true, null) : new CheckoutOutcome(false, result.BlockError("git checkout"));
    }

    // LibGit2Sharp's Patch API drives diff output through native→managed callbacks (per
    // hunk and per line), which the NativeAOT-generated marshalling stubs for GitDiffHunk
    // NRE on. Everything else in libgit2 we use is fine; only diff goes through this
    // callback path. Shell out to `git diff` for diffs to sidestep it entirely.
    public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return DiffError(repo, path, side, "Not a git repository.");

            var contextArg = $"--unified={DiffOptions.ContextLines}";
            string? patchText;
            string? error;

            if (side == DiffSide.Commit)
            {
                if (string.IsNullOrEmpty(commitSha))
                    return DiffError(repo, path, side, "Commit SHA required for commit diff.");

                // `git show` handles root commits and merges correctly; --format= suppresses
                // the commit message header so the output is a plain patch parseable by ParseGitDiff.
                patchText = RunGitDiff(repo.Path, out error,
                    "show", "--no-color", "--format=", "-M", contextArg, commitSha, "--", path);
            }
            else if (side == DiffSide.Staged)
            {
                patchText = RunGitDiff(repo.Path, out error,
                    "diff", "--cached", "--no-color", "-M", contextArg, "--", path);
            }
            else if (IsTracked(repo.Path, path))
            {
                patchText = RunGitDiff(repo.Path, out error,
                    "diff", "--no-color", "-M", contextArg, "--", path);
            }
            else
            {
                // Untracked file: `git diff` ignores it, so render it as an addition by
                // diffing against the platform null device.
                var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                var absPath = Path.IsPathRooted(path) ? path : Path.Combine(repo.Path, path);
                patchText = RunGitDiff(repo.Path, out error,
                    "diff", "--no-color", "--no-index", contextArg, "--", nullPath, absPath);
            }

            if (patchText == null)
                return DiffError(repo, path, side, error ?? "git diff failed.");

            return ParseGitDiff(repo.Id, path, side, patchText);
        }
        catch (Exception ex)
        {
            return DiffError(repo, path, side, ex.Message);
        }
    }

    private string RunGit(string workingDir, out string? error, params string[] args)
        => RunGitInternal(workingDir, allowExitCode1: false, out error, args)!;

    // `git diff --no-index` exits 1 when the two inputs differ — that's normal output, not failure.
    private string? RunGitDiff(string workingDir, out string? error, params string[] args)
        => RunGitInternal(workingDir, allowExitCode1: true, out error, args);

    private string? RunGitInternal(string workingDir, bool allowExitCode1, out string? error, string[] args)
    {
        error = null;
        var result = _runner.Run(workingDir, args, GitProcessRunner.GitLaunch.Direct);
        if (result.Ok || (allowExitCode1 && result.ExitCode == 1)) return result.Stdout;
        error = result.FirstLineError("git");
        return null;
    }

    private bool IsTracked(string workingDir, string path)
    {
        var result = _runner.Run(
            workingDir,
            new[] { "ls-files", "--error-unmatch", "--", path },
            GitProcessRunner.GitLaunch.Direct);
        return result.Ok;
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
        var hunks = new List<DiffHunk>();
        if (string.IsNullOrEmpty(patchText))
            return (hunks, false);

        var totalLines = 0;
        var truncated = false;

        // Build the current hunk incrementally as we walk the patch text.
        int curOldStart = 0, curOldLines = 0, curNewStart = 0, curNewLines = 0;
        string? curHeader = null;
        List<DiffLine>? curLines = null;
        int oldLineCursor = 0, newLineCursor = 0;
        bool inHunk = false;

        void Flush(List<DiffHunk> dst)
        {
            if (!inHunk || curLines == null) return;
            dst.Add(new DiffHunk(curOldStart, curOldLines, curNewStart, curNewLines, curHeader, curLines));
        }

        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@"))
            {
                Flush(hunks);
                if (!TryParseHunkHeader(raw, out curOldStart, out curOldLines, out curNewStart, out curNewLines, out curHeader))
                {
                    inHunk = false;
                    continue;
                }
                curLines = new List<DiffLine>();
                oldLineCursor = curOldStart;
                newLineCursor = curNewStart;
                inHunk = true;
                continue;
            }

            if (!inHunk || curLines == null) continue;
            if (raw.Length == 0) continue;
            if (raw[0] == '\\') continue;

            DiffLine? line = null;
            switch (raw[0])
            {
                case ' ':
                    line = new DiffLine(DiffLineKind.Context, oldLineCursor, newLineCursor, raw.Length > 1 ? raw[1..] : string.Empty);
                    oldLineCursor++;
                    newLineCursor++;
                    break;
                case '+':
                    line = new DiffLine(DiffLineKind.Added, null, newLineCursor, raw.Length > 1 ? raw[1..] : string.Empty);
                    newLineCursor++;
                    break;
                case '-':
                    line = new DiffLine(DiffLineKind.Removed, oldLineCursor, null, raw.Length > 1 ? raw[1..] : string.Empty);
                    oldLineCursor++;
                    break;
            }

            if (line == null) continue;
            if (totalLines >= DiffOptions.TruncationLineCap)
            {
                truncated = true;
                continue;
            }
            curLines.Add(line);
            totalLines++;
        }

        Flush(hunks);
        return (hunks, truncated);
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
    public AbortOperationOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new AbortOperationOutcome(false, "Not a git repository.");

            // Pick the verb. For force-quit we prefer git's own --quit (it removes the
            // sequencer/rebase state without touching the index/workdir) and fall back to
            // direct sentinel removal for ops that don't have one.
            var args = forceQuit ? GetForceQuitArgs(state) : GetAbortArgs(state);
            using var _ = LockRepo(repo.Path);
            string? cmdMsg = null;
            int? exitCode = null;
            if (args != null)
            {
                var result = _runner.Run(repo.Path, args);
                if (!result.Started) return new AbortOperationOutcome(false, "Failed to start git.");
                exitCode = result.ExitCode;
                cmdMsg = GitProcessRunner.CombineGitOutput(result.Stderr, result.Stdout);
            }
            else if (forceQuit)
            {
                // No `git X --quit` for this op (Merge, Bisect, UnmergedPaths) —
                // delete the sentinels ourselves. The user already confirmed they want
                // to abandon HEAD recovery, so this is the documented escape hatch.
                cmdMsg = ForceClearSentinels(repo, state);
                if (cmdMsg != null) return new AbortOperationOutcome(false, cmdMsg);
            }
            else
            {
                return new AbortOperationOutcome(false, "Nothing to abort.");
            }

            // Authoritative check: does git still see an in-progress op? An exit-0
            // result combined with leftover state means the command warned and
            // walked away — typically a malformed sentinel dir from a prior crash.
            var stillStuck = GetOperationState(repo) != RepoOperationState.None;

            if (!stillStuck && (exitCode == null || exitCode == 0))
                return new AbortOperationOutcome(true, null);

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
            return new AbortOperationOutcome(false, msg, ForceQuitAvailable: canForceQuit);
        }
        catch (Exception ex)
        {
            return new AbortOperationOutcome(false, ex.Message);
        }
    }

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

    public ContinueOperationOutcome ContinueOperation(Repo repo, RepoOperationState state)
    {
        try
        {
            if (!IsGitRepo(repo.Path))
                return new ContinueOperationOutcome(false, "Not a git repository.");

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
                return new ContinueOperationOutcome(false,
                    $"Continue isn't supported for {DescribeState(state)}.");

            using var _ = LockRepo(repo.Path);
            var result = _runner.Run(repo.Path, args, configure: static psi =>
            {
                psi.EnvironmentVariables["GIT_EDITOR"] = "true";
                psi.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";
            });
            if (result.Ok) return new ContinueOperationOutcome(true, null);

            bool hasMoreConflicts;
            try { hasMoreConflicts = HasUnmergedPaths(repo.Path); }
            catch { hasMoreConflicts = false; }

            return new ContinueOperationOutcome(false,
                result.BlockError($"git {string.Join(' ', args)}"), HasMoreConflicts: hasMoreConflicts);
        }
        catch (Exception ex)
        {
            return new ContinueOperationOutcome(false, ex.Message);
        }
    }

    private static CommitDetails DetailsError(Repo repo, string sha, string message)
        => new(
            RepoId: repo.Id,
            Sha: sha,
            AuthorName: string.Empty,
            AuthorEmail: string.Empty,
            AuthorWhen: DateTimeOffset.MinValue,
            CommitterName: string.Empty,
            CommitterEmail: string.Empty,
            CommitterWhen: DateTimeOffset.MinValue,
            Message: string.Empty,
            MessageShort: string.Empty,
            ParentShas: Array.Empty<string>(),
            Files: Array.Empty<FileChange>(),
            ErrorMessage: message);
}

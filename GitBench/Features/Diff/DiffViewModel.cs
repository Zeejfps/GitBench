using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Observable;

namespace GitBench.Features.Diff;

public record DiffTarget(string Path, DiffSide Side, string? CommitSha = null);

// Per-pane diff body mode. Sticky on the DiffViewModel: persists across file selection within a
// pane and is toggled via ToggleFullFile(). FullFile shows the after-side whole file with changed
// lines tinted, so a change can be read with full surrounding context.
internal enum DiffViewMode { Diff, FullFile }

internal abstract record DiffRenderState
{
    public sealed record Placeholder(string Text) : DiffRenderState;
    // Highlight is null until the async syntax pass completes (or stays null when highlighting
    // is off/unsupported/failed) — the view renders plain in that case, identical to before.
    public sealed record Loaded(DiffResult Result, DiffHighlight? Highlight = null) : DiffRenderState;
    // Full after-side file. AddedLineNumbers are 1-based new-file line numbers tinted as additions
    // (derived from the diff's Added rows); every other line renders as context. Removed lines are
    // absent — this is the current state of the file. Highlight reuses the new-side spans.
    // Emphasis maps a new-file line number to its intra-line changed-character ranges: the diff has
    // both sides, so the new-side ranges are paired up here (the view can't, it only sees the
    // after-file). Only added lines that paired with a similar removed line appear.
    public sealed record FullFile(
        string Path,
        IReadOnlyList<string> Lines,
        IReadOnlySet<int> AddedLineNumbers,
        DiffSide Side,
        bool Truncated,
        IReadOnlyDictionary<int, IReadOnlyList<CharRange>>? Emphasis = null,
        DiffHighlight? Highlight = null) : DiffRenderState;
    // A conflicted (unmerged) working-tree file. Drives the Fork-style resolution header
    // (two side cards + take ours/theirs/both + open-in-editor) instead of a normal diff.
    public sealed record Conflict(string Path, ConflictContext Context) : DiffRenderState;
}

// Badge shown in the diff header for binary files: whether the blob lives in Git LFS or is
// stored inline. Text diffs and placeholders produce None, which hides the badge.
internal enum LfsBadge { None, Tracked, NotTracked }

internal sealed record DiffState(DiffRenderState Render, string? OpError, DiffViewMode Mode);

internal sealed class DiffViewModel : ViewModelBase<DiffState>
{
    // Fallbacks used only when no localization service is supplied (some embedded panes are
    // constructed before their owner injects one). When _loc is present these are localized.
    private const string EmptyPlaceholder = "Select a file to view diff.";
    private const string LoadingPlaceholder = "Loading…";

    private readonly IReadable<DiffTarget?> _target;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService? _loc;
    // When set, this pane is pinned to a specific repo (a commit diff in the Review window or a
    // pop-out) and resolves it by id, so its diff stays correct no matter which repo is active in
    // the main window. Null ⇒ the pane follows the active repo (Local Changes, History).
    private readonly Guid? _pinnedRepoId;

    private string EmptyText => _loc?.Strings.Value.DiffNoSelection ?? EmptyPlaceholder;
    private string LoadingText => _loc?.Strings.Value.CommonLoading ?? LoadingPlaceholder;
    // Used only for "Open in editor" on a conflict. Null in panes that never show conflicts
    // (commit details, and pop-out windows pinned to a commit diff).
    private readonly IPlatformShell? _shell;
    private bool _deferReloadToWorkingTreeChange;

    // Syntax highlighting runs on its own lane so an in-flight highlight for a file we've
    // navigated away from is dropped, and so it never invalidates the diff-load lane (Gen).
    private readonly GenerationGuard _highlightLane;

    /// <summary>The file this pane is pinned to (path + side + optional commit sha), or null when no
    /// file is selected. Read by the diff-pane header's Viewed toggle to key per-file reviewed state.</summary>
    public IReadable<DiffTarget?> Target => _target;

    public IReadable<DiffRenderState> RenderState { get; }
    public IReadable<string?> OpError { get; }
    public IReadable<LfsBadge> LfsStatus { get; }

    // Sticky body mode for this pane. Drives the toggle button's active state and is read by
    // StartLoad to decide whether to assemble a diff or a full-file render.
    public IReadable<DiffViewMode> Mode { get; }

    // Whether the embedded pane is collapsed to its header strip. The header chevron toggles it;
    // the host nulls the diff body and pins the pane to header height when set. Sticky per-pane
    // like Mode, and unused in the pop-out window (which has no collapse affordance).
    private readonly State<bool> _isCollapsed = new(false);
    public IReadable<bool> IsCollapsed => _isCollapsed;

    // The side of the currently-loaded diff (null until a diff loads). Drives the header's
    // file-level Stage/Unstage button: Unstaged → "Stage file", Staged → "Unstage file",
    // Commit → hidden (history diffs aren't stageable).
    public IReadable<DiffSide?> CurrentSide { get; }

    public DiffViewModel(
        IReadable<DiffTarget?> target,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IPlatformShell? shell = null,
        ILocalizationService? loc = null,
        Guid? pinnedRepoId = null)
        : base(dispatcher, new DiffState(
            new DiffRenderState.Placeholder(loc?.Strings.Value.DiffNoSelection ?? EmptyPlaceholder), null, DiffViewMode.Diff))
    {
        _target = target;
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _shell = shell;
        _loc = loc;
        _pinnedRepoId = pinnedRepoId;
        _highlightLane = CreateLane();

        RenderState = Slice(s => s.Render);
        OpError = Slice(s => s.OpError);
        LfsStatus = Slice(s => s.Render is DiffRenderState.Loaded { Result.IsBinary: true } loaded
            ? (loaded.Result.IsLfs ? LfsBadge.Tracked : LfsBadge.NotTracked)
            : LfsBadge.None);
        CurrentSide = Slice(s => s.Render switch
        {
            DiffRenderState.Loaded l => l.Result.Side,
            DiffRenderState.FullFile ff => ff.Side,
            _ => (DiffSide?)null,
        });
        Mode = Slice(s => s.Mode);

        Subscriptions.Add(_target.Subscribe(_ =>
        {
            if (_deferReloadToWorkingTreeChange) return;
            StartLoad();
        }));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged));

        // A loaded diff draws its localized chrome at paint time (DiffContentView re-resolves the
        // binary/no-changes/conflict labels each frame), but a placeholder bakes its text into the
        // render state, so it would stay in the old language after a live locale switch. Reload to
        // re-resolve it. Guarded to a placeholder so a shown diff isn't needlessly re-fetched.
        if (_loc != null)
        {
            var first = true;
            Subscriptions.Add(_loc.Strings.Subscribe(_ =>
            {
                if (first) { first = false; return; }
                if (State.Value.Render is DiffRenderState.Placeholder)
                    StartLoad();
            }));
        }
    }

    public void DeferReloadToWorkingTreeChange() => _deferReloadToWorkingTreeChange = true;

    // The repo this pane operates on: the pinned repo (resolved by id) when set, else the active repo.
    private Repo? ResolveRepo()
    {
        if (_pinnedRepoId is not { } id) return _registry.Active.Value;
        var active = _registry.Active.Value;
        if (active != null && active.Id == id) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage msg)
    {
        var active = ResolveRepo();
        if (active == null || active.Id != msg.RepoId) return;
        _deferReloadToWorkingTreeChange = false;
        if (_target.Value == null) return;
        StartLoad();
    }

    public void StageHunk(int hunkIndex) => ApplyHunk(hunkIndex, cached: true, reverse: false);

    public void UnstageHunk(int hunkIndex) => ApplyHunk(hunkIndex, cached: true, reverse: true);

    // Pops the current diff into its own top-level window. DiffWindowPresenter handles the
    // message and spins up an independent, live DiffViewModel pinned to this target.
    public void RequestOpenInWindow()
    {
        var target = _target.Value;
        if (target == null) return;
        _bus.Broadcast(new OpenDiffWindowMessage(target, _pinnedRepoId));
    }

    public void ToggleCollapse() => _isCollapsed.Value = !_isCollapsed.Value;

    // Flips this pane between Diff and FullFile, then reloads so the render state is rebuilt for
    // the current target under the new mode. Sticky: the new mode carries to the next file too.
    public void ToggleFullFile()
    {
        Update(s => s with { Mode = s.Mode == DiffViewMode.Diff ? DiffViewMode.FullFile : DiffViewMode.Diff });
        StartLoad();
    }

    public void StageFile() => RunFileIndexOp(stage: true);

    public void UnstageFile() => RunFileIndexOp(stage: false);

    // Stages/unstages the whole current file. Mirrors ApplyHunk's pattern: run the git op
    // off the UI thread, then broadcast a working-tree change so every list (and this diff)
    // re-syncs against the truth — LocalChangesViewModel owns the optimistic list updates.
    private void RunFileIndexOp(bool stage)
    {
        var repo = ResolveRepo();
        if (repo == null) return;
        var target = _target.Value;
        if (target == null) return;

        var path = target.Path;
        var service = _gitService;
        var bus = _bus;
        var dispatcher = Dispatcher;
        var repoId = repo.Id;
        Task.Run(() =>
        {
            GitOutcome outcome;
            try
            {
                outcome = stage
                    ? service.Stage(repo, new[] { path })
                    : service.Unstage(repo, new[] { path });
            }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }

            dispatcher.Post(() =>
            {
                if (outcome is GitOutcome.Failed failed)
                {
                    Update(s => s with { OpError = failed.Message });
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    // Conflict resolution from the resolution header. Each writes the chosen content + stages
    // (git add marks the path resolved), then broadcasts a working-tree change so the file
    // lists, the operation banner, and this diff all re-sync. The reload drops the conflict
    // (now staged), so the pane swaps back to a normal/empty diff automatically.
    public void ResolveTakeOurs() => RunResolve((svc, repo, path) => svc.TakeOurs(repo, path));

    public void ResolveTakeTheirs() => RunResolve((svc, repo, path) => svc.TakeTheirs(repo, path));

    public void ResolveTakeBoth() => RunResolve((svc, repo, path) => svc.TakeBoth(repo, path));

    // Manual-resolution path: stages the working-tree file as-is (git add), or records the
    // deletion (git rm) when the file is gone, marking the conflict resolved without picking a
    // side. For conflicts resolved outside the app (an external editor / merge tool).
    public void ResolveMarkResolved() => RunResolve((svc, repo, path) => svc.MarkResolved(repo, path));

    private void RunResolve(Func<IGitService, Repo, string, GitOutcome> op)
    {
        var repo = ResolveRepo();
        if (repo == null) return;
        if (State.Value.Render is not DiffRenderState.Conflict conflict) return;

        var path = conflict.Path;
        var service = _gitService;
        var bus = _bus;
        var dispatcher = Dispatcher;
        var repoId = repo.Id;
        Task.Run(() =>
        {
            GitOutcome outcome;
            try { outcome = op(service, repo, path); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }

            dispatcher.Post(() =>
            {
                if (outcome is GitOutcome.Failed failed)
                {
                    Update(s => s with { OpError = failed.Message });
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    // Opens the conflicted file in the OS default editor so the user can resolve markers by
    // hand; they then stage it normally to mark it resolved.
    public void OpenConflictInEditor()
    {
        var repo = ResolveRepo();
        if (repo == null || _shell == null) return;
        if (State.Value.Render is not DiffRenderState.Conflict conflict) return;
        _shell.OpenFile(System.IO.Path.Combine(repo.Path, conflict.Path));
    }

    public void RequestDiscardHunk(int hunkIndex)
    {
        if (!TryGetPatchContext(hunkIndex, out var repo, out var diff)) return;
        var patch = HunkPatchBuilder.Build(diff, hunkIndex);
        _bus.Broadcast(new ShowDialogMessage(onClose => new DiscardHunkDialog { Repo = repo, Path = diff.Path, Patch = patch, OnClose = onClose }));
    }

    private void ApplyHunk(int hunkIndex, bool cached, bool reverse)
    {
        if (!TryGetPatchContext(hunkIndex, out var repo, out var diff)) return;
        var patch = HunkPatchBuilder.Build(diff, hunkIndex);

        var isLastHunk = diff.Hunks.Count == 1;
        var fromSide = diff.Side;
        DiffSide? toSide = (cached, reverse) switch
        {
            (true, false) => DiffSide.Staged,    // stage: unstaged → staged
            (true, true) => DiffSide.Unstaged,   // unstage: staged → unstaged
            _ => null,
        };

        // Optimistic diff update — when there are hunks left, drop the just-applied one so
        // the diff repaints immediately. When this was the last hunk and the file moves to
        // another side, show a brief Loading placeholder; the selection swap below will
        // kick off a fresh load for the destination side and replace it.
        if (!isLastHunk)
        {
            var remainingHunks = new List<DiffHunk>(diff.Hunks.Count - 1);
            for (var i = 0; i < diff.Hunks.Count; i++)
                if (i != hunkIndex) remainingHunks.Add(diff.Hunks[i]);
            // Whole-file line numbering is unchanged by dropping a hunk, so the existing spans
            // stay valid — carry them through to avoid a highlight flicker before the reload.
            var highlight = CurrentHighlight();
            Update(s => s with { Render = new DiffRenderState.Loaded(diff with { Hunks = remainingHunks }, highlight) });
        }
        else if (toSide.HasValue)
        {
            Update(s => s with { Render = new DiffRenderState.Placeholder(LoadingText) });
        }

        _bus.Broadcast(new HunkAppliedOptimisticMessage(repo.Id, diff.Path, fromSide, toSide, isLastHunk));

        // Intentionally unguarded: every apply must broadcast a working-tree change so the
        // optimistic move (here and in LocalChangesViewModel) reconciles against the truth,
        // so this op does not run through RunBackground's staleness drop.
        var service = _gitService;
        var bus = _bus;
        var dispatcher = Dispatcher;
        var repoId = repo.Id;
        var original = diff;
        Task.Run(() =>
        {
            GitOutcome outcome;
            try { outcome = service.ApplyPatch(repo, patch, cached, reverse); }
            catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }

            dispatcher.Post(() =>
            {
                if (outcome is GitOutcome.Failed failed)
                {
                    Update(s => s with { OpError = failed.Message });
                    // Roll back the optimistic diff state, and broadcast a working-tree
                    // change so LocalChangesViewModel re-syncs its lists against the truth
                    // (we may have optimistically moved the file in OnHunkAppliedOptimistic).
                    if (State.Value.Render is DiffRenderState.Loaded)
                        Update(s => s with { Render = new DiffRenderState.Loaded(original, CurrentHighlight()) });
                    bus.Broadcast(new WorkingTreeChangedMessage(repoId));
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    private DiffHighlight? CurrentHighlight()
        => State.Value.Render is DiffRenderState.Loaded l ? l.Highlight : null;

    // Carries the still-current render's highlight onto a freshly-loaded render for the same file,
    // so the diff keeps its syntax colors instead of dropping to plain until the async highlight
    // pass re-runs. Read inside StartLoad's onResult before the render is swapped, so State still
    // holds the previous render. Only seeds when the incoming render has no highlight of its own.
    private DiffRenderState CarryHighlightForward(DiffRenderState next)
    {
        (string Path, DiffHighlight Highlight)? prev = State.Value.Render switch
        {
            DiffRenderState.Loaded { Highlight: { } h } l => (l.Result.Path, h),
            DiffRenderState.FullFile { Highlight: { } h } ff => (ff.Path, h),
            _ => null,
        };
        if (prev is not { } p) return next;
        return next switch
        {
            DiffRenderState.Loaded { Highlight: null } l when l.Result.Path == p.Path => l with { Highlight = p.Highlight },
            DiffRenderState.FullFile { Highlight: null } ff when ff.Path == p.Path => ff with { Highlight = p.Highlight },
            _ => next,
        };
    }

    private bool TryGetPatchContext(int hunkIndex, out Repo repo, out DiffResult diff)
    {
        repo = null!;
        diff = null!;
        var active = ResolveRepo();
        if (active == null) return false;
        if (State.Value.Render is not DiffRenderState.Loaded loaded) return false;
        if (!HunkPatchBuilder.CanPatchHunk(loaded.Result)) return false;
        if (hunkIndex < 0 || hunkIndex >= loaded.Result.Hunks.Count) return false;
        repo = active;
        diff = loaded.Result;
        return true;
    }

    private void StartLoad()
    {
        // Any in-flight highlight is for the previous target; invalidate it up front so its
        // result can't land on the diff we're about to load.
        _highlightLane.Bump();

        var target = _target.Value;
        if (target == null)
        {
            Gen.Bump();
            Update(s => s with { Render = new DiffRenderState.Placeholder(EmptyText) });
            return;
        }

        var repo = ResolveRepo();
        if (repo == null) return;

        if (State.Value.Render is not (DiffRenderState.Loaded or DiffRenderState.FullFile))
            Update(s => s with { Render = new DiffRenderState.Placeholder(LoadingText) });

        var path = target.Path;
        var side = target.Side;
        var commitSha = target.CommitSha;
        var mode = State.Value.Mode;
        var git = _gitService;
        // Capture localized placeholder text up front so the background worker doesn't touch the
        // observable off the UI thread.
        var binaryText = _loc?.Strings.Value.DiffBinaryNotShown ?? "Binary file not shown";
        var noVersionText = _loc?.Strings.Value.DiffNoCurrentVersion ?? "File has no current version";
        // Always load the diff: it supplies the added-line set for full-file tinting and drives the
        // highlight pass for both modes. In FullFile mode we additionally fetch the whole new-side
        // file off the same worker thread.
        RunBackground<LoadResult>(
            work: () =>
            {
                // A conflicted working-tree file gets the resolution header, not a normal diff —
                // but only in Diff mode. Toggling to FullFile escapes the header to show the raw
                // working-tree file (conflict markers and all). GetConflictContext is cheap (one
                // `ls-files -u`) and returns null for the common non-conflict case.
                if (side == DiffSide.Unstaged && mode == DiffViewMode.Diff)
                {
                    var conflict = git.GetConflictContext(repo, path);
                    if (conflict != null)
                        return (new LoadResult(new DiffRenderState.Conflict(path, conflict), null), null);
                }

                var diff = git.GetDiff(repo, path, side, commitSha);
                if (mode == DiffViewMode.Diff)
                    return (new LoadResult(new DiffRenderState.Loaded(diff), diff), null);
                var render = BuildFullFile(git, repo, diff, path, side, commitSha, binaryText, noVersionText);
                return (new LoadResult(render, render is DiffRenderState.FullFile ? diff : null), null);
            },
            onResult: (result, error) =>
            {
                var render = error != null ? new DiffRenderState.Placeholder(error) : result!.Render;
                // Seed the new render with the prior highlight when it's for the same file, so the
                // diff doesn't blink to plain between this load landing and the async highlight pass
                // finishing. Staging a file is the common case: the file moves to the other side but
                // its new-side text — hence the spans — is unchanged, so the body doesn't flash but
                // the highlight would. StartHighlight below refreshes the spans either way.
                render = CarryHighlightForward(render);
                Update(s => s with { Render = render });
                // Highlight applies to either render carrying the new-side file; result.Diff is null
                // for full-file placeholders (binary/deleted), which need no highlighting.
                if (result?.Diff is { } diff && render is DiffRenderState.Loaded or DiffRenderState.FullFile)
                    StartHighlight(repo, diff, commitSha);
            });
    }

    // Result of a load: the render to show plus the source diff (when one should drive a highlight
    // pass). Diff is null for full-file placeholders, where there's nothing to highlight.
    private sealed record LoadResult(DiffRenderState Render, DiffResult? Diff);

    // Assembles a FullFile render from a loaded diff: fetches the after-side file text, caps it,
    // and marks which lines were added. Returns a Placeholder for cases with no readable current
    // version (binary, diff error, or a deleted/absent file).
    private static DiffRenderState BuildFullFile(
        IGitService git, Repo repo, DiffResult diff, string path, DiffSide side, string? commitSha,
        string binaryText, string noVersionText)
    {
        if (diff.IsBinary) return new DiffRenderState.Placeholder(binaryText);
        if (diff.ErrorMessage != null) return new DiffRenderState.Placeholder(diff.ErrorMessage);

        var text = git.GetFileText(repo, path, side, oldSide: false, commitSha);
        if (text == null) return new DiffRenderState.Placeholder(noVersionText);

        var lines = SplitLines(text);
        var truncated = false;
        if (lines.Count > DiffOptions.TruncationLineCap)
        {
            lines.RemoveRange(DiffOptions.TruncationLineCap, lines.Count - DiffOptions.TruncationLineCap);
            truncated = true;
        }

        var added = new HashSet<int>();
        Dictionary<int, IReadOnlyList<CharRange>>? emphasis = null;
        foreach (var hunk in diff.Hunks)
        {
            // Intra-line pairing needs both sides; do it here while the diff is in hand, keyed by
            // new-line number so the after-file view can attach the new-side ranges per row.
            IReadOnlyList<CharRange>?[]? hunkEmphasis = null;
            if (DiffOptions.IntraLineHighlightingEnabled)
            {
                var expanded = new string[hunk.Lines.Count];
                for (var i = 0; i < hunk.Lines.Count; i++)
                    expanded[i] = DiffText.ExpandTabs(hunk.Lines[i].Text);
                hunkEmphasis = IntraLineDiff.ForHunk(hunk.Lines, expanded);
            }
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                if (line.Kind != DiffLineKind.Added || line.NewLineNumber is not int n) continue;
                added.Add(n);
                if (hunkEmphasis?[i] is { Count: > 0 } ranges)
                    (emphasis ??= new Dictionary<int, IReadOnlyList<CharRange>>())[n] = ranges;
            }
        }

        return new DiffRenderState.FullFile(path, lines, added, side, truncated, emphasis);
    }

    // Splits file text into display lines, normalizing CRLF/CR to LF and dropping the single empty
    // element a trailing newline produces (so a file ending in "\n" doesn't show a phantom row).
    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = new List<string>(normalized.Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Tokenizes the diff's file(s) off-thread and, when done, re-emits the same Loaded state
    // carrying the spans. Runs on the highlight lane (stale results dropped) and only attaches
    // to the still-current diff — an optimistic hunk apply may have swapped Result underneath us.
    private void StartHighlight(Repo repo, DiffResult diff, string? commitSha)
    {
        var git = _gitService;
        RunBackground<DiffHighlight>(
            work: () => (DiffHighlightCoordinator.Compute(git, repo, diff, commitSha), null),
            onResult: (highlight, _) =>
            {
                if (highlight == null) return; // plain rendering — nothing to apply
                // Re-attach only to the still-current render for this diff. Loaded matches by
                // Result reference; FullFile has no Result, so match on Path + Side (an optimistic
                // hunk apply or a mode toggle may have swapped the render underneath us).
                switch (State.Value.Render)
                {
                    case DiffRenderState.Loaded cur when ReferenceEquals(cur.Result, diff):
                        Update(s => s with { Render = new DiffRenderState.Loaded(diff, highlight) });
                        break;
                    case DiffRenderState.FullFile ff when ff.Path == diff.Path && ff.Side == diff.Side:
                        Update(s => s with { Render = ff with { Highlight = highlight } });
                        break;
                }
            },
            lane: _highlightLane);
    }
}

using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

// Render phase of a review window: resolving the range, a centered message (empty range / error), or
// a resolved range whose combined change list drives the two-column body.
internal abstract record ReviewRenderState
{
    public sealed record Loading : ReviewRenderState;
    public sealed record Placeholder(string Text) : ReviewRenderState;
    public sealed record Loaded(ReviewStack Stack) : ReviewRenderState;
}

// What the window body should show, folding the range resolution and the combined file list's own
// load phase into one signal for the view's Switch.
internal enum ReviewContentKind { Loading, Message, Loaded }

// The single adaptive primary action. ViewFile = "mark this file viewed and open the next unviewed
// one"; Complete = every file in the range is viewed, nothing left to do.
internal enum ReviewPrimaryAction { ViewFile, Complete }

internal sealed record ReviewState(ReviewRenderState Render);

// A snapshot of everything the header HUD renders: how many of the range's files are viewed, whether
// the review is complete, and which primary action applies. Recomputed (as a Derived) from the loaded
// file list, the Viewed tracker, and the active tab — so it refreshes on load and on every toggle.
internal sealed record ReviewHud(
    int FilesViewed,
    int FilesTotal,
    bool IsComplete,
    ReviewPrimaryAction Primary,
    bool HasActiveFile)
{
    public float FilesFraction => FilesTotal == 0 ? 0f : (float)FilesViewed / FilesTotal;
    public bool HasFiles => FilesTotal > 0;
}

/// <summary>
/// One open review window, pinned to a single <see cref="ReviewSession"/>. Resolves the range through
/// <see cref="IReviewStackSource"/> and shows it as one combined base→head change list — the PR-style
/// overview — driving the reused commit-details surface via its own
/// <see cref="CommitDetailsViewModel"/>. Tracks which files the reviewer has marked Viewed (ephemeral
/// for the window's lifetime) and offers step-through navigation over the unviewed ones.
/// </summary>
internal sealed class ReviewWindowViewModel : ViewModelBase<ReviewState>
{
    private const int StackCap = 200;

    private readonly IReviewStackSource _source;
    private readonly CommitDetailsViewModel _details;
    private readonly ILocalizationService _loc;
    private readonly IRepoSnapshotStore _snapshots;
    private readonly ReviewedFileTracker _reviewedFiles = new();

    // The reviewer's in-window base override (the header base dropdown); null = auto-resolve. Only
    // the base varies — the window stays pinned to its repo + head. Setting it re-resolves the range
    // through the reload lane.
    private readonly State<string?> _baseOverride = new(null);

    // Refs-driven reload runs on its own lane so it never invalidates an in-flight first load (or
    // vice versa), and stale-while-revalidate: it keeps the current range on screen until the new
    // one arrives.
    private readonly GenerationGuard _reloadLane;

    // The keyboard cheatsheet overlay's visibility. Window-ephemeral; toggled by the '?' key and the
    // header's help button.
    private readonly State<bool> _cheatsheetOpen = new(false);

    // Whether the combined file list has ever finished loading, so ContentKind holds the window on
    // its loading state through the first load but keeps the current content up during a reload
    // (stale-while-revalidate).
    private readonly State<bool> _detailsEverLoaded = new(false);

    public ReviewSession Session { get; }
    public string Title { get; }

    public IReadable<bool> CheatsheetOpen => _cheatsheetOpen;

    // Per-window store of marked-Viewed files, provided into the window subtree so the reused
    // diff-pane header and Changes list show their Viewed marks. Owned (and disposed) here.
    public IReviewedFileTracker ReviewedFiles => _reviewedFiles;

    // The window's own commit-details VM, provided into the split's sub-context; both columns (the
    // file tree and the diff tabs) bind to it. It does not subscribe to commit selection, so the
    // History pane can never drive it. Owned here and disposed with the window.
    public CommitDetailsViewModel Details => _details;

    public IReadable<ReviewContentKind> ContentKind { get; }
    public IReadable<string> PlaceholderText { get; }

    // The base side of the header range: the resolved ref name (e.g. "origin/main") once loaded, or
    // "Resolving base…" while the first stack loads. Rendered as a clickable chip that opens the base
    // dropdown. The head side is read straight off the pinned Session.
    public IReadable<string> BaseChipLabel { get; }

    // Provenance for the base chip's hover tooltip ("N commits since head forked from origin/main
    // (sha) · upstream"). Null until the range resolves.
    public IReadable<string?> BaseTooltip { get; }

    public IReadable<string> FilesViewedLabel { get; }

    // The aggregate header state (progress, primary action). The views bind their pieces off this
    // single Derived so they all refresh together.
    public IReadable<ReviewHud> Hud { get; }

    // 0..1 progress of the header meter: files viewed across the combined range.
    public IReadable<float> FilesFraction { get; }

    // Gates the header's primary button: disabled once every file is viewed (nothing to do).
    public IReadable<bool> PrimaryActionEnabled { get; }

    // The adaptive primary action's localized label, derived off the HUD so it tracks the active tab
    // and every Viewed toggle. The icon stays in the view (glyphs aren't localized).
    public IReadable<string> PrimaryActionLabel { get; }

    public ReviewWindowViewModel(
        ReviewSession session,
        IReviewStackSource source,
        IUiDispatcher dispatcher,
        CommitDetailsViewModel details,
        ILocalizationService loc,
        IMessageBus bus,
        IRepoSnapshotStore snapshots)
        : base(dispatcher, new ReviewState(new ReviewRenderState.Loading()))
    {
        Session = session;
        _source = source;
        _details = details;
        _loc = loc;
        _snapshots = snapshots;
        _reloadLane = CreateLane();
        Subscriptions.Add(_cheatsheetOpen);
        Subscriptions.Add(_baseOverride);
        Subscriptions.Add(_detailsEverLoaded);
        Title = loc.Strings.Value.ReviewWindowTitle(session.HeadLabel);

        // ContentKind and PlaceholderText fold in the combined file list's own load phase: the window
        // stays on its loading state until the first list lands, surfaces a load failure as the
        // window message, and holds Loaded through reloads (stale-while-revalidate).
        var contentKind = new Derived<ReviewContentKind>(BuildContentKind);
        ContentKind = contentKind;
        Subscriptions.Add(contentKind);

        var placeholderText = new Derived<string>(BuildPlaceholderText);
        PlaceholderText = placeholderText;
        Subscriptions.Add(placeholderText);

        BaseChipLabel = Slice(BuildBaseChipLabel);
        BaseTooltip = Slice<string?>(BuildBaseTooltip);

        // Combines the loaded file list and the Viewed tracker, so it refreshes on load and on every
        // Viewed toggle.
        var filesViewedLabel = new Derived<string>(BuildFilesViewedLabel);
        FilesViewedLabel = filesViewedLabel;
        Subscriptions.Add(filesViewedLabel);

        var hud = new Derived<ReviewHud>(BuildHud);
        Hud = hud;
        Subscriptions.Add(hud);

        var filesFraction = new Derived<float>(() => hud.Value.FilesFraction);
        FilesFraction = filesFraction;
        Subscriptions.Add(filesFraction);

        var primaryEnabled = new Derived<bool>(() => hud.Value.Primary != ReviewPrimaryAction.Complete);
        PrimaryActionEnabled = primaryEnabled;
        Subscriptions.Add(primaryEnabled);

        var primaryLabel = new Derived<string>(() => BuildPrimaryActionLabel(hud.Value));
        PrimaryActionLabel = primaryLabel;
        Subscriptions.Add(primaryLabel);

        Subscriptions.Add(_details.RenderState.Subscribe(r =>
        {
            if (r is CommitDetailsRenderState.Loaded && !_detailsEverLoaded.Value)
                _detailsEverLoaded.Value = true;
        }));

        // A ref change in the reviewed repo (amend, rebase, push, branch move) reshapes the range;
        // reload it without dropping the current view. Working-tree edits never touch committed
        // history, so they're deliberately ignored here.
        Subscriptions.Add(bus.SubscribeScoped<RefsChangedMessage>(m =>
        {
            if (m.RepoId == Session.RepoId) Reload();
        }));

        StartLoad();
    }

    // Re-resolves the range against a new base (null = auto) without re-pinning the window's repo or
    // head. Reuses the refs-change reload lane: stale-while-revalidate (the current range stays on
    // screen until the new one arrives).
    public void SetBase(string? baseRef)
    {
        if (_baseOverride.Value == baseRef) return;
        _baseOverride.Value = baseRef;
        Reload();
    }

    // Candidate bases for the header dropdown: Auto (the smart default), then the repo's local and
    // remote branches — the head under review excluded (reviewing against itself is an empty range).
    // A check marks the current choice. Built fresh per open from the already-loaded branch snapshot,
    // so the click never blocks on git. (Reads the active repo's branches — consistent with the
    // window's existing active-repo scoping.) The menu itself adds the search box and scrolling.
    public IReadOnlyList<RepoBarContextMenu.Item> BuildBaseMenuItems()
    {
        var s = _loc.Strings.Value;
        var current = _baseOverride.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.ReviewBaseMenuAuto, () => SetBase(null),
                Icon: current == null ? LucideIcons.CircleCheck : null),
        };

        if ((_snapshots.Branches.Value as Fetched<BranchListing>.Ok)?.Value is not { } listing)
            return items;

        var locals = listing.LocalBranches.Where(b => b.Name != Session.HeadRef).ToList();
        if (locals.Count > 0)
        {
            items.Add(RepoBarContextMenu.Separator);
            foreach (var b in locals)
            {
                var name = b.Name;
                items.Add(new RepoBarContextMenu.Item(name, () => SetBase(name),
                    Icon: current == name ? LucideIcons.CircleCheck : null));
            }
        }

        foreach (var group in listing.Remotes)
        {
            if (group.Branches.Count == 0) continue;
            items.Add(RepoBarContextMenu.Separator);
            foreach (var b in group.Branches)
            {
                var refName = $"{group.Name}/{b.Name}";
                items.Add(new RepoBarContextMenu.Item(refName, () => SetBase(refName),
                    Icon: current == refName ? LucideIcons.CircleCheck : null));
            }
        }

        return items;
    }

    // The base the window currently reviews against: the in-window override (the dropdown), falling
    // back to the session's pinned base, else null = auto-resolve. Only the base varies.
    private ReviewSession EffectiveSession()
    {
        var baseRef = _baseOverride.Value ?? Session.BaseRef;
        return baseRef == null
            ? Session with { BaseRef = null, BaseLabel = null }
            : Session with { BaseRef = baseRef, BaseLabel = baseRef };
    }

    // Flips the active file's Viewed mark — the 'v' key's reversible toggle, the same one the
    // diff-pane header button drives. No-op when no file tab is open.
    public void ToggleActiveFileViewed()
    {
        var key = RangeKey();
        var active = _details.SelectedPath.Value;
        if (key == null || active == null) return;
        _reviewedFiles.ToggleViewed(key, active);
    }

    public void ToggleCheatsheet() => _cheatsheetOpen.Value = !_cheatsheetOpen.Value;
    public void CloseCheatsheet() => _cheatsheetOpen.Value = false;

    // Opens the next / previous file's tab within the range (the j / k keys). Clamped at the
    // first/last file; no-op while the file list hasn't loaded.
    public void NextFile() => StepFile(+1);
    public void PrevFile() => StepFile(-1);

    private void StepFile(int delta)
    {
        var files = Files();
        if (files.Count == 0) return;
        var active = _details.SelectedPath.Value;
        int next;
        if (active == null)
            next = delta > 0 ? 0 : files.Count - 1;
        else
        {
            var index = IndexOfFile(files, active);
            next = index < 0 ? 0 : Math.Clamp(index + delta, 0, files.Count - 1);
        }
        _details.SelectFile(files[next].Path);
    }

    // The one adaptive control (the header button and Enter/Space): mark-and-advance through the
    // unviewed files, or do nothing once the whole range is viewed.
    public void RunPrimaryAction()
    {
        if (Hud.Value.Primary == ReviewPrimaryAction.ViewFile)
            MarkActiveFileViewedAndAdvance();
    }

    // Marks the active file Viewed, opens the next unviewed file's tab, then closes the just-reviewed
    // file's tab — the tabbed analog of GitHub's collapse-and-move-on: a reviewed file gets out of the
    // way. Selecting the next tab before closing the old one avoids a transient flash back to Details.
    // When no unviewed file remains the cursor falls to a neighbouring tab (or Details) and the
    // primary action flips to "Review complete". The reviewed state lives in the tracker, so the file
    // still reads checked/dimmed in the Changes list and reopens on demand.
    public void MarkActiveFileViewedAndAdvance()
    {
        var key = RangeKey();
        if (key == null) return;
        var active = _details.SelectedPath.Value;
        if (active == null)
        {
            AdvanceToNextUnviewedFile();
            return;
        }
        if (!_reviewedFiles.IsViewed(key, active))
            _reviewedFiles.ToggleViewed(key, active);

        var next = NextUnviewedFile(key, active);
        if (next != null) _details.SelectFile(next);
        _details.CloseTab(active);
    }

    // Opens the next unviewed file's tab without touching the current one. No-op (stay put) when
    // every file is already viewed.
    public void AdvanceToNextUnviewedFile()
    {
        var key = RangeKey();
        if (key == null) return;
        var next = NextUnviewedFile(key, _details.SelectedPath.Value);
        if (next != null) _details.SelectFile(next);
    }

    // The next unviewed file's path, searching forward from anchorPath and wrapping once within the
    // range. Null when the range has no files or all are viewed.
    private string? NextUnviewedFile(string key, string? anchorPath)
    {
        var files = Files();
        if (files.Count == 0) return null;
        var start = anchorPath == null ? -1 : IndexOfFile(files, anchorPath);
        for (var step = 1; step <= files.Count; step++)
        {
            var i = (start + step) % files.Count;
            if (i < 0) i += files.Count;
            if (!_reviewedFiles.IsViewed(key, files[i].Path))
                return files[i].Path;
        }
        return null;
    }

    // Disposes the owned Viewed tracker and the window's commit-details VM (no view owns it in the
    // two-column layout), then the base (slices/subscriptions).
    public override void Dispose()
    {
        _reviewedFiles.Dispose();
        _details.Dispose();
        base.Dispose();
    }

    private void StartLoad()
    {
        Update(s => s with { Render = new ReviewRenderState.Loading() });
        var session = EffectiveSession();
        RunBackground<ReviewStack>(
            // The source is async by contract; bridging through RunBackground's worker keeps the
            // proven staleness/dispatcher handling.
            work: () => (_source.LoadAsync(session, StackCap).GetAwaiter().GetResult(), null),
            onResult: (stack, error) =>
            {
                if (error != null)
                {
                    Update(s => s with { Render = new ReviewRenderState.Placeholder(error) });
                    return;
                }
                if (stack == null || stack.Increments.Count == 0)
                {
                    Update(s => s with
                    {
                        Render = new ReviewRenderState.Placeholder(_loc.Strings.Value.ReviewEmptyRange),
                    });
                    return;
                }

                // Drive the details VM to the combined net diff before flipping to Loaded so the
                // split mounts already loading.
                _details.ShowRange(Session.RepoId, stack.BaseSha, stack.HeadSha);
                Update(s => s with { Render = new ReviewRenderState.Loaded(stack) });
            });
    }

    // Reloads the range after a ref change without disturbing the current view until the result is
    // in (stale-while-revalidate): a transient failure keeps the existing range on screen.
    private void Reload()
    {
        var session = EffectiveSession();
        RunBackground<ReviewStack>(
            work: () => (_source.LoadAsync(session, StackCap).GetAwaiter().GetResult(), null),
            onResult: (stack, error) =>
            {
                if (error != null || stack == null) return;
                ApplyReloadedStack(stack);
            },
            lane: _reloadLane);
    }

    private void ApplyReloadedStack(ReviewStack stack)
    {
        if (stack.Increments.Count == 0)
        {
            Update(s => s with { Render = new ReviewRenderState.Placeholder(_loc.Strings.Value.ReviewEmptyRange) });
            return;
        }

        var current = CurrentStack();
        Update(s => s with { Render = new ReviewRenderState.Loaded(stack) });

        // Re-drive only when the resolved endpoints moved: identical shas mean an identical net diff
        // (sha identity == content identity), and re-showing it would close the reviewer's open tabs
        // for nothing.
        if (current == null || current.BaseSha != stack.BaseSha || current.HeadSha != stack.HeadSha)
            _details.ShowRange(Session.RepoId, stack.BaseSha, stack.HeadSha);
    }

    private ReviewContentKind BuildContentKind()
    {
        switch (State.Value.Render)
        {
            case ReviewRenderState.Loading:
                return ReviewContentKind.Loading;
            case ReviewRenderState.Placeholder:
                return ReviewContentKind.Message;
        }
        return _details.RenderState.Value switch
        {
            CommitDetailsRenderState.Placeholder => ReviewContentKind.Message,
            CommitDetailsRenderState.Loading when !_detailsEverLoaded.Value => ReviewContentKind.Loading,
            _ => ReviewContentKind.Loaded,
        };
    }

    private string BuildPlaceholderText()
    {
        if (State.Value.Render is ReviewRenderState.Placeholder p) return p.Text;
        if (State.Value.Render is ReviewRenderState.Loaded
            && _details.RenderState.Value is CommitDetailsRenderState.Placeholder dp)
            return dp.Text;
        return string.Empty;
    }

    // The base side of the header range. Once loaded it's the resolved ref name; before that the
    // real base ref is unknown, so show the pinned/overridden label if any, else "Resolving base…"
    // rather than a bare "auto"/SHA.
    private string BuildBaseChipLabel(ReviewState s)
    {
        if (s.Render is ReviewRenderState.Loaded l) return l.Stack.BaseLabel;
        return _baseOverride.Value ?? Session.BaseLabel ?? _loc.Strings.Value.ReviewBaseResolving;
    }

    // "N commits since {head} forked from {ref} ({sha})" plus the kind (upstream / default branch)
    // for an auto-resolved base. Null until the range resolves (nothing to explain yet).
    private string? BuildBaseTooltip(ReviewState s)
    {
        if (s.Render is not ReviewRenderState.Loaded l) return null;
        var stack = l.Stack;
        var provenance = _loc.Strings.Value.ReviewBaseProvenance(
            stack.Increments.Count, Session.HeadLabel, stack.BaseLabel, ShortSha(stack.BaseSha));
        var kindWord = stack.BaseKind switch
        {
            ReviewBaseKind.Upstream => _loc.Strings.Value.ReviewBaseKindUpstream,
            ReviewBaseKind.DefaultBranch => _loc.Strings.Value.ReviewBaseKindDefault,
            _ => null,
        };
        return kindWord == null ? provenance : $"{provenance} · {kindWord}";
    }

    private static string ShortSha(string sha) => sha.Length <= 7 ? sha : sha[..7];

    // "X / Y files viewed" for the combined range. Empty until a non-empty file list is loaded.
    private string BuildFilesViewedLabel()
    {
        var key = RangeKey();
        if (key == null) return string.Empty;
        var files = Files();
        if (files.Count == 0) return string.Empty;
        return _loc.Strings.Value.ReviewFilesViewed(CountViewed(key, files), files.Count);
    }

    // The adaptive primary action's localized label. "Mark viewed → next file" while reviewing a
    // file (or "Review files" when sitting on the Details tab), "Review complete" at the end.
    private string BuildPrimaryActionLabel(ReviewHud hud)
    {
        var s = _loc.Strings.Value;
        return hud.Primary switch
        {
            ReviewPrimaryAction.ViewFile => hud.HasActiveFile ? s.ReviewActionMarkViewedNext : s.ReviewActionReviewFiles,
            _ => s.ReviewComplete,
        };
    }

    private ReviewHud BuildHud()
    {
        var key = RangeKey();
        var files = Files();
        var viewed = key == null ? 0 : CountViewed(key, files);
        var complete = files.Count > 0 && viewed >= files.Count;
        var detailsLoading = _details.RenderState.Value is CommitDetailsRenderState.Loading;
        var hasActiveFile = _details.SelectedPath.Value != null;

        // While the file list is still loading hold on ViewFile so the button doesn't flash
        // "complete" mid-load; a genuinely empty net diff has nothing left to do.
        var primary = complete || (files.Count == 0 && !detailsLoading)
            ? ReviewPrimaryAction.Complete
            : ReviewPrimaryAction.ViewFile;

        return new ReviewHud(
            FilesViewed: viewed,
            FilesTotal: files.Count,
            IsComplete: complete,
            Primary: primary,
            HasActiveFile: hasActiveFile);
    }

    private int CountViewed(string key, IReadOnlyList<FileChange> files)
    {
        _ = _reviewedFiles.Revision.Value;
        var viewed = 0;
        foreach (var f in files)
            if (_reviewedFiles.IsViewed(key, f.Path)) viewed++;
        return viewed;
    }

    private ReviewStack? CurrentStack() =>
        State.Value.Render is ReviewRenderState.Loaded l ? l.Stack : null;

    // The tracker key for the loaded range's per-file Viewed marks — the same base..head key the open
    // tabs and diff headers derive, so every surface reads and writes the same mark.
    private string? RangeKey() =>
        CurrentStack() is { } stack ? ReviewFileKey.ForRange(stack.BaseSha, stack.HeadSha) : null;

    // The combined range's files, available once the details surface has loaded them.
    private IReadOnlyList<FileChange> Files() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : Array.Empty<FileChange>();

    private static int IndexOfFile(IReadOnlyList<FileChange> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
            if (files[i].Path == path) return i;
        return -1;
    }
}

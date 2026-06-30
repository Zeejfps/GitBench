using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

// Render phase of a review window: loading the stack, a centered message (empty range / error), or a
// loaded stack ready to step through. Mirrors CommitDetailsRenderState's shape.
internal abstract record ReviewRenderState
{
    public sealed record Loading : ReviewRenderState;
    public sealed record Placeholder(string Text) : ReviewRenderState;
    public sealed record Loaded(ReviewStack Stack) : ReviewRenderState;
}

// What the window body should show, derived from ReviewRenderState for the view's Switch.
internal enum ReviewContentKind { Loading, Message, Loaded }

// The single adaptive control by the diff. ViewFile = "mark this file viewed and open the next
// unviewed one"; NextIncrement = the current increment is fully viewed, step to the next commit;
// Complete = every increment reviewed, nothing left to do.
internal enum ReviewPrimaryAction { ViewFile, NextIncrement, Complete }

internal sealed record ReviewState(
    ReviewRenderState Render,
    string? SelectedSha,
    IReadOnlySet<string> ReviewedShas);

// A snapshot of everything the header bar and action bar render: file/increment progress, the
// current increment's position, whether the ends of the stack are reached, and which primary
// action applies. Recomputed (as a Derived) from review state, the loaded file list, the Viewed
// tracker, and the active tab — so the whole HUD refreshes on selection, load, and every toggle.
internal sealed record ReviewHud(
    int FilesViewed,
    int FilesTotal,
    int IncrementsReviewed,
    int IncrementsTotal,
    int IncrementIndex,
    bool CanPrev,
    bool CanNext,
    bool IsComplete,
    ReviewPrimaryAction Primary,
    bool HasActiveFile)
{
    public float FilesFraction => FilesTotal == 0 ? 0f : (float)FilesViewed / FilesTotal;
    public float IncrementsFraction => IncrementsTotal == 0 ? 0f : (float)IncrementsReviewed / IncrementsTotal;
    public bool HasFiles => FilesTotal > 0;
}

/// <summary>
/// One open review window, pinned to a single <see cref="ReviewSession"/>. Loads the stack through
/// <see cref="IReviewStackSource"/>, auto-selects the first (base-most) increment, and drives the
/// reused commit-details surface via its own <see cref="CommitDetailsViewModel"/>. Tracks which
/// increments the reviewer has marked reviewed (ephemeral for the window's lifetime) and offers
/// step-through navigation over the unreviewed ones.
/// </summary>
internal sealed class ReviewWindowViewModel : ViewModelBase<ReviewState>
{
    private const int StackCap = 200;
    private static readonly IReadOnlySet<string> NoneReviewed = new HashSet<string>();

    private readonly IReviewStackSource _source;
    private readonly CommitDetailsViewModel _details;
    private readonly ILocalizationService _loc;
    private readonly ReviewedFileTracker _reviewedFiles = new();

    // Refs-driven reload runs on its own lane so it never invalidates an in-flight first load (or
    // vice versa), and stale-while-revalidate: it keeps the current stack on screen until the new
    // one arrives.
    private readonly GenerationGuard _reloadLane;

    // The keyboard cheatsheet overlay's visibility. Window-ephemeral; toggled by the '?' key and the
    // header's help button.
    private readonly State<bool> _cheatsheetOpen = new(false);

    public ReviewSession Session { get; }
    public string Title { get; }

    public IReadable<bool> CheatsheetOpen => _cheatsheetOpen;

    // Per-window store of marked-Viewed files, provided into the window subtree so the reused
    // diff-pane header shows its Viewed toggle. Owned (and disposed) here.
    public IReviewedFileTracker ReviewedFiles => _reviewedFiles;

    // The window's own commit-details VM, provided into the right pane's sub-context. It does not
    // subscribe to commit selection, so the History pane can never drive it. Its lifetime is owned
    // by the reused CommitDetailsView (which disposes it via UseViewModel on unmount), so this VM
    // must not dispose it again — hence no Dispose override here.
    public CommitDetailsViewModel Details => _details;

    public IReadable<ReviewContentKind> ContentKind { get; }
    public IReadable<string> PlaceholderText { get; }
    public IReadable<string?> SelectedSha { get; }
    public IReadable<IReadOnlySet<string>> ReviewedShas { get; }
    public IReadable<IReadOnlyList<ReviewIncrement>> Increments { get; }
    public IReadable<string> RangeText { get; }
    public IReadable<string> IncrementLabel { get; }
    public IReadable<string> ReviewedLabel { get; }
    public IReadable<string> FilesViewedLabel { get; }

    // The aggregate header/action-bar state (progress, position, primary action). The views bind
    // their pieces off this single Derived so they all refresh together.
    public IReadable<ReviewHud> Hud { get; }

    // Whether a base-ward / tip-ward increment exists from the current selection — gates the header's
    // ‹ › nav buttons. Depend only on review state, so a cheap Slice covers them.
    public IReadable<bool> CanSelectPrev { get; }
    public IReadable<bool> CanSelectNext { get; }

    // 0..1 progress for the two meters: increments reviewed across the stack (header) and files viewed
    // within the selected increment (action bar).
    public IReadable<float> IncrementsFraction { get; }
    public IReadable<float> FilesFraction { get; }

    // Gates the action-bar primary button: disabled once the whole stack is reviewed (nothing to do).
    public IReadable<bool> PrimaryActionEnabled { get; }

    // The adaptive primary action's localized label, derived off the HUD so it tracks selection and
    // every Viewed toggle. The icon stays in the view (glyphs aren't localized).
    public IReadable<string> PrimaryActionLabel { get; }

    public ReviewWindowViewModel(
        ReviewSession session,
        IReviewStackSource source,
        IUiDispatcher dispatcher,
        CommitDetailsViewModel details,
        ILocalizationService loc,
        IMessageBus bus)
        : base(dispatcher, new ReviewState(new ReviewRenderState.Loading(), null, NoneReviewed))
    {
        Session = session;
        _source = source;
        _details = details;
        _loc = loc;
        _reloadLane = CreateLane();
        Subscriptions.Add(_cheatsheetOpen);
        Title = loc.Strings.Value.ReviewWindowTitle(session.HeadLabel);

        ContentKind = Slice(s => s.Render switch
        {
            ReviewRenderState.Loading => ReviewContentKind.Loading,
            ReviewRenderState.Placeholder => ReviewContentKind.Message,
            _ => ReviewContentKind.Loaded,
        });
        PlaceholderText = Slice(s => s.Render is ReviewRenderState.Placeholder p ? p.Text : string.Empty);
        SelectedSha = Slice(s => s.SelectedSha);
        ReviewedShas = Slice(s => s.ReviewedShas);
        Increments = Slice(s => s.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>());
        RangeText = Slice(BuildRangeText);
        IncrementLabel = Slice(BuildIncrementLabel);
        ReviewedLabel = Slice(BuildReviewedLabel);
        CanSelectPrev = Slice(s => SelectedIndex(s) > 0);
        CanSelectNext = Slice(s =>
        {
            var increments = s.Render is ReviewRenderState.Loaded l ? l.Stack.Increments.Count : 0;
            var index = SelectedIndex(s);
            return index >= 0 && index < increments - 1;
        });

        // Combines own state, the selected increment's loaded file list, and the Viewed tracker, so it
        // refreshes on selection, on details load, and on every Viewed toggle.
        var filesViewedLabel = new Derived<string>(BuildFilesViewedLabel);
        FilesViewedLabel = filesViewedLabel;
        Subscriptions.Add(filesViewedLabel);

        var hud = new Derived<ReviewHud>(BuildHud);
        Hud = hud;
        Subscriptions.Add(hud);

        IncrementsFraction = Slice(s =>
        {
            if (s.Render is not ReviewRenderState.Loaded l || l.Stack.Increments.Count == 0) return 0f;
            return (float)s.ReviewedShas.Count / l.Stack.Increments.Count;
        });
        var filesFraction = new Derived<float>(() => hud.Value.FilesFraction);
        FilesFraction = filesFraction;
        Subscriptions.Add(filesFraction);

        var primaryEnabled = new Derived<bool>(() => hud.Value.Primary != ReviewPrimaryAction.Complete);
        PrimaryActionEnabled = primaryEnabled;
        Subscriptions.Add(primaryEnabled);

        var primaryLabel = new Derived<string>(() => BuildPrimaryActionLabel(hud.Value));
        PrimaryActionLabel = primaryLabel;
        Subscriptions.Add(primaryLabel);

        // Viewing the last file of the selected increment marks the increment reviewed (one-way:
        // un-viewing a file doesn't revoke the increment's reviewed mark, which can also be set by hand).
        Subscriptions.Add(_reviewedFiles.Revision.Subscribe(_ => MarkIncrementIfAllFilesViewed()));
        Subscriptions.Add(_details.RenderState.Subscribe(_ => MarkIncrementIfAllFilesViewed()));

        // A ref change in the reviewed repo (amend, rebase, push, branch move) reshapes the stack;
        // reload it without dropping the current view. Working-tree edits never touch committed
        // history, so they're deliberately ignored here.
        Subscriptions.Add(bus.SubscribeScoped<RefsChangedMessage>(m =>
        {
            if (m.RepoId == Session.RepoId) Reload();
        }));

        StartLoad();
    }

    // Selects an increment and drives the right pane to its commit-vs-parent diff. Dedupes so a
    // re-click of the current row is a no-op.
    public void SelectIncrement(string sha)
    {
        if (State.Value.SelectedSha == sha) return;
        Update(s => s with { SelectedSha = sha });
        _details.Show(Session.RepoId, sha);
    }

    // Flips an increment's reviewed mark. Ephemeral: cleared when the window closes (Phase 5a).
    // Per-file Viewed state and persistence land later.
    public void ToggleReviewed(string sha)
    {
        Update(s =>
        {
            var next = new HashSet<string>(s.ReviewedShas);
            if (!next.Remove(sha)) next.Add(sha);
            return s with { ReviewedShas = next };
        });
    }

    // Flips the active file's Viewed mark — the 'v' key's reversible toggle, the same one the
    // diff-pane header button drives. No-op when no file tab is open.
    public void ToggleActiveFileViewed()
    {
        var sha = State.Value.SelectedSha;
        var active = _details.SelectedPath.Value;
        if (sha == null || active == null) return;
        _reviewedFiles.ToggleViewed(sha, active);
    }

    public void ToggleCheatsheet() => _cheatsheetOpen.Value = !_cheatsheetOpen.Value;
    public void CloseCheatsheet() => _cheatsheetOpen.Value = false;

    // Marks the selected increment reviewed, then jumps to the next increment that still needs review
    // (the action-bar "Next increment"). Going to the next *unreviewed* increment — not the strict
    // sequential neighbour — marks an empty increment as it's passed and lets a finished tip send the
    // reviewer back to an earlier gap, so the loop reaches "complete" instead of dead-ending.
    public void MarkReviewedAndAdvance()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return;
        Update(s => s with { ReviewedShas = new HashSet<string>(s.ReviewedShas) { sha } });
        NextUnreviewed();
    }

    // Selects the next unreviewed increment after the current selection, wrapping past the tip back
    // to the base. No-op when every increment is reviewed (or the stack is empty).
    public void NextUnreviewed()
    {
        var list = CurrentIncrements();
        if (list.Count == 0) return;
        var reviewed = State.Value.ReviewedShas;
        var start = IndexOf(list, State.Value.SelectedSha);
        for (var step = 1; step <= list.Count; step++)
        {
            var candidate = list[(start + step) % list.Count];
            if (!reviewed.Contains(candidate.Sha))
            {
                SelectIncrement(candidate.Sha);
                return;
            }
        }
    }

    // Steps one increment toward the base / tip without wrapping (the header's ‹ › cluster and the
    // [ / ] keys). Clamped at the ends, where the matching nav control is disabled.
    public void SelectPrevIncrement() => StepIncrement(-1);
    public void SelectNextIncrement() => StepIncrement(+1);

    private void StepIncrement(int delta)
    {
        var list = CurrentIncrements();
        var index = IndexOf(list, State.Value.SelectedSha);
        if (index < 0) return;
        var next = index + delta;
        if (next < 0 || next >= list.Count) return;
        SelectIncrement(list[next].Sha);
    }

    // Opens the next / previous file's tab within the selected increment (the j / k keys). Clamped at
    // the first/last file; no-op when the increment has no files loaded yet.
    public void NextFile() => StepFile(+1);
    public void PrevFile() => StepFile(-1);

    private void StepFile(int delta)
    {
        var files = SelectedFiles();
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

    // The single adaptive control by the diff (the action-bar button and Enter/Space): mark-and-advance
    // within an increment, hop to the next increment once it's fully viewed, or do nothing when the whole
    // stack is reviewed.
    public void RunPrimaryAction()
    {
        switch (Hud.Value.Primary)
        {
            case ReviewPrimaryAction.ViewFile:
                MarkActiveFileViewedAndAdvance();
                break;
            case ReviewPrimaryAction.NextIncrement:
                MarkReviewedAndAdvance();
                break;
            case ReviewPrimaryAction.Complete:
                break;
        }
    }

    // Marks the active file Viewed, opens the next unviewed file's tab, then closes the just-reviewed
    // file's tab — the tabbed analog of GitHub's collapse-and-move-on: a reviewed file gets out of the
    // way. Selecting the next tab before closing the old one avoids a transient flash back to Details.
    // Stops at the increment edge: when no unviewed file remains the cursor falls to a neighbouring tab
    // (or Details) and the primary action flips to "Next increment". The reviewed state lives in the
    // tracker, so the file still reads checked/dimmed in the Changes list and reopens on demand.
    public void MarkActiveFileViewedAndAdvance()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return;
        var active = _details.SelectedPath.Value;
        if (active == null)
        {
            AdvanceToNextUnviewedFile();
            return;
        }
        if (!_reviewedFiles.IsViewed(sha, active))
            _reviewedFiles.ToggleViewed(sha, active);

        var next = NextUnviewedFile(sha, active);
        if (next != null) _details.SelectFile(next);
        _details.CloseTab(active);
    }

    // Opens the next unviewed file's tab in the selected increment without touching the current one.
    // No-op (stay put) when every file is already viewed.
    public void AdvanceToNextUnviewedFile()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return;
        var next = NextUnviewedFile(sha, _details.SelectedPath.Value);
        if (next != null) _details.SelectFile(next);
    }

    // The next unviewed file's path in the selected increment, searching forward from anchorPath and
    // wrapping once within the increment. Null when the increment has no files or all are viewed.
    private string? NextUnviewedFile(string sha, string? anchorPath)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return null;
        var start = anchorPath == null ? -1 : IndexOfFile(files, anchorPath);
        for (var step = 1; step <= files.Count; step++)
        {
            var i = (start + step) % files.Count;
            if (i < 0) i += files.Count;
            if (!_reviewedFiles.IsViewed(sha, files[i].Path))
                return files[i].Path;
        }
        return null;
    }

    // Disposes the owned Viewed tracker, then the base (slices/subscriptions). The shared _details VM
    // is owned by the reused CommitDetailsView and must not be disposed here.
    public override void Dispose()
    {
        _reviewedFiles.Dispose();
        base.Dispose();
    }

    private void StartLoad()
    {
        Update(s => s with { Render = new ReviewRenderState.Loading() });
        RunBackground<ReviewStack>(
            // The source is async by contract; the stub completes synchronously, so bridging through
            // RunBackground's worker keeps the proven staleness/dispatcher handling. A truly async
            // git source (Phase 4) can move off this bridge.
            work: () => (_source.LoadAsync(Session, StackCap).GetAwaiter().GetResult(), null),
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

                // Land on the first (base-most) increment so the right pane opens on something.
                // Drive the details VM before flipping to Loaded so the pane mounts already loading.
                var first = stack.Increments[0].Sha;
                _details.Show(Session.RepoId, first);
                Update(s => s with { Render = new ReviewRenderState.Loaded(stack), SelectedSha = first });
            });
    }

    // Reloads the stack after a ref change without disturbing the current view until the result is
    // in (stale-while-revalidate): a transient failure keeps the existing stack on screen. On
    // success the selection is preserved when its commit survives, and reviewed marks are pruned to
    // the surviving commits.
    private void Reload()
    {
        RunBackground<ReviewStack>(
            work: () => (_source.LoadAsync(Session, StackCap).GetAwaiter().GetResult(), null),
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

        var shas = new HashSet<string>(stack.Increments.Select(i => i.Sha));
        var current = State.Value.SelectedSha;
        var selected = current != null && shas.Contains(current) ? current : stack.Increments[0].Sha;
        var reviewed = new HashSet<string>(State.Value.ReviewedShas.Where(shas.Contains));

        Update(s => s with
        {
            Render = new ReviewRenderState.Loaded(stack),
            SelectedSha = selected,
            ReviewedShas = reviewed,
        });

        // Re-drive the right pane only when the selection moved; a surviving commit's diff is
        // immutable (sha identity == content identity), so re-showing it would needlessly reload.
        if (selected != current) _details.Show(Session.RepoId, selected);
    }

    private string BuildRangeText(ReviewState s)
    {
        var baseLabel = s.Render is ReviewRenderState.Loaded l
            ? l.Stack.BaseLabel
            : (Session.BaseLabel ?? _loc.Strings.Value.ReviewBaseAuto);
        return $"{baseLabel} → {Session.HeadLabel}";
    }

    private string BuildIncrementLabel(ReviewState s)
    {
        if (s.Render is not ReviewRenderState.Loaded l) return string.Empty;
        var total = l.Stack.Increments.Count;
        if (total == 0 || s.SelectedSha == null) return string.Empty;
        var index = IndexOf(l.Stack.Increments, s.SelectedSha);
        return index < 0 ? string.Empty : _loc.Strings.Value.ReviewIncrementPosition(index + 1, total);
    }

    private string BuildReviewedLabel(ReviewState s)
    {
        if (s.Render is not ReviewRenderState.Loaded l) return string.Empty;
        return _loc.Strings.Value.ReviewIncrementsReviewed(s.ReviewedShas.Count, l.Stack.Increments.Count);
    }

    // "X / Y files viewed" for the selected increment. Empty until a non-empty increment is loaded.
    private string BuildFilesViewedLabel()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null) return string.Empty;
        var files = SelectedFiles();
        if (files.Count == 0) return string.Empty;

        _ = _reviewedFiles.Revision.Value;
        var viewed = 0;
        foreach (var f in files)
            if (_reviewedFiles.IsViewed(sha, f.Path)) viewed++;
        return _loc.Strings.Value.ReviewFilesViewed(viewed, files.Count);
    }

    // The adaptive primary action's localized label. "Mark viewed → next file" while reviewing a
    // file (or "Review files" when sitting on the Details tab), "Next increment" once the increment
    // is fully viewed, "Review complete" at the end.
    private string BuildPrimaryActionLabel(ReviewHud hud)
    {
        var s = _loc.Strings.Value;
        return hud.Primary switch
        {
            ReviewPrimaryAction.ViewFile => hud.HasActiveFile ? s.ReviewActionMarkViewedNext : s.ReviewActionReviewFiles,
            ReviewPrimaryAction.NextIncrement => s.ReviewActionNextIncrement,
            _ => s.ReviewComplete,
        };
    }

    private ReviewHud BuildHud()
    {
        var s = State.Value;
        var increments = s.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>();
        var total = increments.Count;
        var index = IndexOf(increments, s.SelectedSha);
        var files = SelectedFiles();

        _ = _reviewedFiles.Revision.Value;
        var sha = s.SelectedSha;
        var filesViewed = 0;
        if (sha != null)
            foreach (var f in files)
                if (_reviewedFiles.IsViewed(sha, f.Path)) filesViewed++;

        var reviewedCount = s.ReviewedShas.Count;
        var complete = total > 0 && reviewedCount >= total;
        var hasActiveFile = _details.SelectedPath.Value != null;
        var detailsLoading = _details.RenderState.Value is CommitDetailsRenderState.Loading;
        var primary = ComputePrimaryAction(s, increments, index, files, filesViewed, complete, detailsLoading);

        return new ReviewHud(
            FilesViewed: filesViewed,
            FilesTotal: files.Count,
            IncrementsReviewed: reviewedCount,
            IncrementsTotal: total,
            IncrementIndex: index,
            CanPrev: index > 0,
            CanNext: index >= 0 && index < total - 1,
            IsComplete: complete,
            Primary: primary,
            HasActiveFile: hasActiveFile);
    }

    private ReviewPrimaryAction ComputePrimaryAction(
        ReviewState s,
        IReadOnlyList<ReviewIncrement> increments,
        int index,
        IReadOnlyList<FileChange> files,
        int filesViewed,
        bool complete,
        bool detailsLoading)
    {
        if (complete) return ReviewPrimaryAction.Complete;
        if (increments.Count == 0 || index < 0 || s.SelectedSha is not { } sha)
            return ReviewPrimaryAction.Complete;

        var currentDone = s.ReviewedShas.Contains(sha) || (files.Count > 0 && filesViewed >= files.Count);
        if (currentDone) return ReviewPrimaryAction.NextIncrement;
        // No files loaded: while the increment's details are still loading, hold on ViewFile so the
        // button doesn't flash "Next increment" mid-load; a genuinely empty increment can only advance.
        if (files.Count == 0) return detailsLoading ? ReviewPrimaryAction.ViewFile : ReviewPrimaryAction.NextIncrement;
        return ReviewPrimaryAction.ViewFile;
    }

    private void MarkIncrementIfAllFilesViewed()
    {
        var sha = State.Value.SelectedSha;
        if (sha == null || State.Value.ReviewedShas.Contains(sha)) return;
        var files = SelectedFiles();
        if (files.Count == 0) return;

        var viewed = _reviewedFiles.ViewedPaths(sha);
        foreach (var f in files)
            if (!viewed.Contains(f.Path)) return;
        Update(s => s with { ReviewedShas = new HashSet<string>(s.ReviewedShas) { sha } });
    }

    // The selected increment's files, available once its details have loaded into the right pane.
    private IReadOnlyList<FileChange> SelectedFiles() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : Array.Empty<FileChange>();

    private static int SelectedIndex(ReviewState s) =>
        s.Render is ReviewRenderState.Loaded l ? IndexOf(l.Stack.Increments, s.SelectedSha) : -1;

    private IReadOnlyList<ReviewIncrement> CurrentIncrements() =>
        State.Value.Render is ReviewRenderState.Loaded l
            ? l.Stack.Increments
            : Array.Empty<ReviewIncrement>();

    private static int IndexOf(IReadOnlyList<ReviewIncrement> list, string? sha)
    {
        if (sha == null) return -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Sha == sha) return i;
        return -1;
    }

    private static int IndexOfFile(IReadOnlyList<FileChange> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
            if (files[i].Path == path) return i;
        return -1;
    }
}

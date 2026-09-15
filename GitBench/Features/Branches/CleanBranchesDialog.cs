using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

// Why a local branch is offered for cleanup, mirrored from LocalUpstream:
// Disconnected = upstream was set but the remote ref is gone; NeverPushed = no upstream.
internal enum BranchCleanupKind { Disconnected, NeverPushed }

internal readonly record struct CleanBranchCandidate(string Name, BranchCleanupKind Kind);

/// <summary>
/// Confirmation modal for cleaning up stale local branches under a folder. Offers a checkbox
/// per cleanup category — disconnected (upstream deleted) and never-pushed (no upstream) — and
/// previews the exact branches the current selection targets so the destructive delete is never
/// blind. "Delete even if not fully merged" maps to git's force flag; with it off, unmerged
/// branches are skipped rather than removed.
/// </summary>
internal sealed record CleanBranchesDialog : Widget
{
    public required Repo Repo { get; init; }

    /// The folder the cleanup is scoped to; empty for the "Local" root. Shown to the user so the
    /// sub-folder scoping is visible.
    public required string FolderPath { get; init; }

    public required IReadOnlyList<CleanBranchCandidate> Candidates { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var candidates = Candidates;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Localization();
        var s = loc.Strings.Value;

        var disconnectedCount = candidates.Count(c => c.Kind == BranchCleanupKind.Disconnected);
        var neverPushedCount = candidates.Count(c => c.Kind == BranchCleanupKind.NeverPushed);

        // Pre-select the safer set: disconnected branches were usually merged on a now-deleted
        // PR, so default them on; never-pushed branches may be only-local work, so default off.
        var cleanDisconnected = new State<bool>(disconnectedCount > 0);
        var cleanNeverPushed = new State<bool>(false);
        var force = new State<bool>(true);

        // Branch names the user has individually unchecked while their category is still enabled.
        var excludedNames = new State<IReadOnlySet<string>>(new HashSet<string>());

        bool IsKindSelected(BranchCleanupKind kind) => kind switch
        {
            BranchCleanupKind.Disconnected => cleanDisconnected.Value,
            BranchCleanupKind.NeverPushed => cleanNeverPushed.Value,
            _ => false,
        };

        // The rows shown in the dialog — candidates whose category is enabled. Kept independent of
        // the per-branch unchecks so toggling one branch doesn't rebuild (and re-seed) the whole list.
        var visibleCandidates = new Derived<IReadOnlyList<CleanBranchCandidate>>(() =>
            candidates.Where(c => IsKindSelected(c.Kind)).ToList());

        var selectedNames = new Derived<IReadOnlyList<string>>(() =>
        {
            var excluded = excludedNames.Value;
            return visibleCandidates.Value
                .Where(c => !excluded.Contains(c.Name))
                .Select(c => c.Name)
                .ToList();
        });

        var selectedHeader = new Derived<string>(() =>
        {
            var count = selectedNames.Value.Count;
            var strings = loc.Strings.Value;
            return count == 0 ? strings.BranchesCleanNoneSelected : strings.BranchesCleanSelectedHeader(count);
        });

        var actionLabel = new Derived<string>(() => loc.Strings.Value.BranchesCleanAction(selectedNames.Value.Count));
        var canClean = new Derived<bool>(() => selectedNames.Value.Count > 0);

        void ToggleBranch(string name)
        {
            var next = new HashSet<string>(excludedNames.Value);
            if (!next.Add(name)) next.Remove(name);
            excludedNames.Value = next;
        }

        // Carries per-branch failures out of the background delete loop into the UI-thread success
        // callback — same single-writer-before-read pattern as DeleteLocalBranchDialog.
        List<string>? failures = null;

        string? DoClean()
        {
            var forceDelete = force.Value;
            var failed = new List<string>();
            var anySuccess = false;

            foreach (var c in candidates)
            {
                if (!IsKindSelected(c.Kind) || excludedNames.Value.Contains(c.Name)) continue;
                GitOutcome outcome;
                try { outcome = gitService.DeleteBranch(repo, c.Name, forceDelete); }
                catch (Exception ex) { outcome = new GitOutcome.Failed(ex.Message); }
                if (outcome is GitOutcome.Failed f) failed.Add($"{c.Name}: {f.Message}");
                else anySuccess = true;
            }

            // Nothing deleted (e.g. Force off and none merged): surface inline and keep the dialog
            // open so the user can enable Force and retry. Otherwise close and report the stragglers.
            if (!anySuccess && failed.Count > 0)
                return string.Join("\n", failed);

            failures = failed.Count > 0 ? failed : null;
            return null;
        }

        void OnCleanSucceeded()
        {
            bus.Broadcast(new RefsChangedMessage(repo.Id));
            onClose();
            if (failures is { } stragglers)
                bus.Broadcast(new ShowOperationErrorMessage(loc.Strings.Value.BranchesCleanErrorTitle, string.Join("\n", stragglers)));
        }

        var clean = new AsyncCommand(ctx.Require<IUiDispatcher>(), DoClean, OnCleanSucceeded, canClean);

        var body = new List<IWidget>
        {
            new Text
            {
                Value = s.BranchesCleanDescription,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.BodyText),
            },
        };

        if (FolderPath.Length > 0)
        {
            body.Add(new Text
            {
                Value = s.BranchesCleanScope(FolderPath),
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            });
        }

        if (disconnectedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanDisconnectedLabel(disconnectedCount),
                Checked = cleanDisconnected,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        if (neverPushedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanNeverPushedLabel(neverPushedCount),
                Checked = cleanNeverPushed,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        body.Add(new CheckboxWidget
        {
            Label = s.BranchesCleanForceLabel,
            Checked = force,
            Height = Sizes.RowHeight,
        }.WithController<KbmController>());
        body.Add(new Text
        {
            Value = s.BranchesCleanForceHint,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
        });

        body.Add(new Text
        {
            Value = Prop.Bind(selectedHeader),
            Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
        });
        body.Add(new Grow
        {
            Child = new Raw
            {
                View = BuildPreview(ctx, visibleCandidates, candidate =>
                    BuildBranchRow(candidate, !excludedNames.Value.Contains(candidate.Name), ToggleBranch)),
            },
        });

        return new Dialog
        {
            Title = s.BranchesCleanTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Height = 600f,
            BodyGap = 10,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            BindActionLabel = actionLabel,
            Command = clean,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }

    // A checkable row per branch in the preview, so the user can spare individual branches that
    // meet the category criteria. Each carries the same kind badge the tree uses — an orange
    // cloud-off for a disconnected (deleted upstream) branch, a dim branch glyph for a never-pushed
    // one — so the two are easy to tell apart. The local State seeds from the current selection
    // before Changed is wired, so the initial paint doesn't fire a phantom toggle.
    private static IWidget BuildBranchRow(CleanBranchCandidate candidate, bool initiallyChecked, Action<string> toggle)
    {
        var isChecked = new State<bool>(initiallyChecked);
        isChecked.Changed += _ => toggle(candidate.Name);

        var (glyph, color) = candidate.Kind == BranchCleanupKind.Disconnected
            ? (LucideIcons.CloudOff, Theme.Color(t => t.BranchesView.BehindColor))
            : (LucideIcons.Branch, Theme.Color(t => t.BranchesView.RowTextDim));

        return new CheckboxWidget
        {
            Checked = isChecked,
            Height = Sizes.RowHeight,
            Content = new Row
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Text
                    {
                        Value = glyph,
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = BranchIconSize,
                        Width = BranchIconSize + 2f,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = color,
                    },
                    new Grow
                    {
                        Child = new Text
                        {
                            Value = candidate.Name,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(t => t.DialogBody.BodyText),
                        },
                    },
                ],
            },
        }.WithController<KbmController>();
    }

    // Matches the branch/cloud glyph size the tree renders (TextStyles.Icon's default).
    private const float BranchIconSize = 14f;

    private static View BuildPreview(
        Context ctx,
        IReadable<IReadOnlyList<CleanBranchCandidate>> visibleCandidates,
        Func<CleanBranchCandidate, IWidget> row)
    {
        var column = new Column<CleanBranchCandidate>
        {
            Gap = Spacing.Hair,
            Items = Prop.Bind(visibleCandidates),
            Template = row,
        }.BuildView(ctx);

        return new DialogScrollList { Content = column }.BuildView(ctx);
    }
}

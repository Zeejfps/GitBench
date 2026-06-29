using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
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

/// <summary>
/// Confirmation modal for cleaning up stale local branches under a folder. Offers a checkbox
/// per cleanup category — disconnected (upstream deleted) and never-pushed (no upstream) — and
/// previews the exact branches the current selection targets so the destructive delete is never
/// blind. "Delete even if not fully merged" maps to git's force flag; with it off, unmerged
/// branches are skipped rather than removed.
/// </summary>
internal sealed record CleanBranchesDialog : Widget
{
    // Rows the preview shows before it scrolls internally. Lowest of the file-list dialogs because
    // this one carries the most body chrome above it (description, up to two category checkboxes,
    // the force checkbox, and the force hint), so a taller list would push the dialog past the
    // window and hand scrolling to the frame's outer bar instead of the preview's own.
    private const int MaxVisibleRows = 5;

    public required Repo Repo { get; init; }

    /// The folder the cleanup is scoped to; empty for the "Local" root. Shown to the user so the
    /// sub-folder scoping is visible.
    public required string FolderPath { get; init; }

    public required IReadOnlyList<CleanBranchCandidate> Candidates { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CleanBranchesDialogViewModel(
            Repo,
            Candidates,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
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

        if (vm.DisconnectedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanDisconnectedLabel(vm.DisconnectedCount),
                Checked = vm.CleanDisconnected,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        if (vm.NeverPushedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanNeverPushedLabel(vm.NeverPushedCount),
                Checked = vm.CleanNeverPushed,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        body.Add(new CheckboxWidget
        {
            Label = s.BranchesCleanForceLabel,
            Checked = vm.Force,
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
            Value = Prop.Bind(vm.SelectedHeader),
            Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
        });
        body.Add(new Raw { View = BuildPreview(ctx, vm, Candidates.Count) });

        return new Dialog
        {
            Title = s.BranchesCleanTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            BodyGap = 10,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            BindActionLabel = vm.ActionLabel,
            Command = vm.Clean,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }

    // A checkable row per branch in the preview, so the user can spare individual branches that
    // meet the category criteria. Each carries the same kind badge the tree uses — an orange
    // cloud-off for a disconnected (deleted upstream) branch, a dim branch glyph for a never-pushed
    // one — so the two are easy to tell apart. The local State seeds from the VM before Changed is
    // wired, so the initial paint doesn't fire a phantom toggle.
    private static IWidget BuildBranchRow(CleanBranchesDialogViewModel vm, CleanBranchCandidate candidate)
    {
        var isChecked = new State<bool>(vm.IsBranchChecked(candidate.Name));
        isChecked.Changed += _ => vm.ToggleBranch(candidate.Name);

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
                            Overflow = TextOverflow.Ellipsis,
                            Color = Theme.Color(t => t.DialogBody.BodyText),
                        },
                    },
                ],
            },
        }.WithController<KbmController>();
    }

    // Matches the branch/cloud glyph size the tree renders (TextStyles.Icon's default).
    private const float BranchIconSize = 14f;

    private static View BuildPreview(Context ctx, CleanBranchesDialogViewModel vm, int candidateCount)
    {
        var theme = ctx.Theme();

        var column = new Column<CleanBranchCandidate>
        {
            Gap = Spacing.Hair,
            Items = Prop.Bind(vm.VisibleCandidates),
            Template = candidate => BuildBranchRow(vm, candidate),
        }.BuildView(ctx);

        // See DiscardChangesDialog: the frame's own scroll region lays the body out at its natural
        // height, so a Grow can't bound this list. An explicit height (honored at measure time,
        // unlike MaxHeightConstraint) caps the card to MaxVisibleRows and scrolls internally past
        // that. Sized for the full candidate set — the most the category toggles can reveal — so the
        // card height stays put as categories are checked and unchecked.
        var visibleRows = Math.Min(Math.Max(candidateCount, 1), MaxVisibleRows);
        var listHeight = visibleRows * Sizes.RowHeight + (visibleRows - 1) * Spacing.Hair;

        var host = new RectView
        {
            Height = listHeight + 2 * Spacing.Sm + 2f,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(Spacing.Sm),
                    Children =
                    {
                        new DialogScrollRegion { Content = new Raw { View = column } }.BuildView(ctx),
                    },
                },
            },
        };
        host.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        host.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

        return host;
    }
}

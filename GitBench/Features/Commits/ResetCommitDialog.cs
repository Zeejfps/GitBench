using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Confirmation modal shown when the user picks "Reset … to here" on a commit and the
/// working tree has local changes. Mirrors Fork's layout: a "Branch:" / "Move to:" /
/// "Reset type:" stack, with the reset mode picked via a coloured-dot dropdown (green
/// soft, amber mixed, red hard) so the destructiveness reads at a glance.
/// </summary>
internal sealed record ResetCommitDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Summary { get; init; }
    public required string? BranchName { get; init; }
    public required int StagedCount { get; init; }
    public required int UnstagedCount { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var sha = Sha;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Localization();

        var mode = new State<ResetMode>(ResetMode.Mixed);
        var reset = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.ResetCurrent(repo, sha, mode.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(loc.Strings.Value.ToastBranchReset)));
                onClose();
            });

        var s = loc.Strings.Value;
        return new Dialog
        {
            Title = s.CommitsResetTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.CommitsResetAction, DialogButtonRole.Destructive),
            Command = reset,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = BranchName != null
                        ? s.CommitsResetDescWithBranch(BranchName)
                        : s.CommitsResetDescDetached },
                new Text
                {
                    Value = BuildDirtyHint(s, StagedCount, UnstagedCount),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = s.CommitsResetBranchLabel, Value = BranchValue(BranchName, s) },
                new LabeledRow { Label = s.CommitsResetMoveToLabel, Value = CommitValue(ShortSha, Summary) },
                new LabeledRow { Label = s.CommitsResetModeLabel, Value = ResetModeDropdown(ctx, mode) },
            ],
        };
    }

    private static IWidget BranchValue(string? branchName, Strings s) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Default,
                Width = Sizes.Icon,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(t => t.DialogBody.BodyText),
            },
            new Text
            {
                Value = branchName ?? s.CommitsResetDetachedLabel,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(t => t.DialogFrame.TitleText),
            },
        ],
    };

    private static IWidget CommitValue(string shortSha, string summary) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = "●",
                FontSize = FontSize.Caption,
                Width = Sizes.Icon,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Text
            {
                Value = shortSha,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogFrame.TitleText),
            },
            new Grow
            {
                Child = new Clipped
                {
                    Child = new Text
                    {
                        Value = summary,
                        VAlign = TextAlignment.Center,
                        Wrap = TextWrap.NoWrap,
                        Color = Theme.Color(s => s.DialogBody.BodyText),
                    },
                },
            },
        ],
    };

    // Order: safest → most destructive (Fork uses Soft / Mixed / Hard top-to-bottom).
    private static IWidget ResetModeDropdown(Context ctx, State<ResetMode> mode)
    {
        var s = ctx.Localization().Strings.Value;
        var status = ctx.Theme().Styles.Value.Status;
        return new OptionDropdown<ResetMode>
        {
            Selected = mode,
            Options =
            [
                (ResetMode.Soft, s.CommitsResetModeSoft, s.CommitsResetModeSoftDesc),
                (ResetMode.Mixed, s.CommitsResetModeMixed, s.CommitsResetModeMixedDesc),
                (ResetMode.Hard, s.CommitsResetModeHard, s.CommitsResetModeHardDesc),
            ],
            DotColor = m => m switch
            {
                ResetMode.Soft => status.Success,
                ResetMode.Hard => status.Danger,
                _ => status.WarningSoft,
            },
        };
    }

    private static string BuildDirtyHint(Strings s, int staged, int unstaged)
    {
        if (staged > 0 && unstaged > 0) return s.CommitsResetDirtyBoth(staged, unstaged);
        if (staged > 0) return s.CommitsResetDirtyStaged(staged);
        if (unstaged > 0) return s.CommitsResetDirtyUnstaged(unstaged);
        return string.Empty;
    }
}

using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
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
    internal const uint SoftColor = 0xFF57F287;
    internal const uint MixedColor = 0xFFE6A85C;
    internal const uint HardColor = 0xFFED4245;

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
        var vm = new ResetCommitDialogViewModel(
            new ResetCommitRequest(Repo, Sha),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.CommitsResetTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = (s.CommitsResetAction, DialogButtonRole.Destructive),
            Command = vm.Reset,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = BranchName != null
                        ? s.CommitsResetDescWithBranch(BranchName)
                        : s.CommitsResetDescDetached,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = BuildDirtyHint(s, StagedCount, UnstagedCount),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = s.CommitsResetBranchLabel, Value = BranchValue(BranchName, s) },
                new LabeledRow { Label = s.CommitsResetMoveToLabel, Value = CommitValue(ShortSha, Summary) },
                new LabeledRow { Label = s.CommitsResetModeLabel, Value = new ResetModeDropdown { Selected = vm.Mode } },
            ],
        };
    }

    private static IWidget BranchValue(string? branchName, Strings s) => new Row
    {
        Gap = 6,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                Width = 16,
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
        Gap = 8,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = "●",
                FontSize = 10,
                Width = 16,
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

    private static string BuildDirtyHint(Strings s, int staged, int unstaged)
    {
        if (staged > 0 && unstaged > 0) return s.CommitsResetDirtyBoth(staged, unstaged);
        if (staged > 0) return s.CommitsResetDirtyStaged(staged);
        if (unstaged > 0) return s.CommitsResetDirtyUnstaged(unstaged);
        return string.Empty;
    }
}

internal sealed record ResetModeDropdown : Widget
{
    public required State<ResetMode> Selected { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        // Order: safest → most destructive (Fork uses Soft / Mixed / Hard top-to-bottom).
        (ResetMode Mode, string Label, string Detail, uint Color)[] options =
        {
            (ResetMode.Soft, s.CommitsResetModeSoft, s.CommitsResetModeSoftDesc, ResetCommitDialog.SoftColor),
            (ResetMode.Mixed, s.CommitsResetModeMixed, s.CommitsResetModeMixedDesc, ResetCommitDialog.MixedColor),
            (ResetMode.Hard, s.CommitsResetModeHard, s.CommitsResetModeHardDesc, ResetCommitDialog.HardColor),
        };

        string LookupLabel(ResetMode m)
        {
            foreach (var o in options) if (o.Mode == m) return o.Label;
            return string.Empty;
        }

        string LookupDetail(ResetMode m)
        {
            foreach (var o in options) if (o.Mode == m) return o.Detail;
            return string.Empty;
        }

        uint LookupColor(ResetMode m)
        {
            foreach (var o in options) if (o.Mode == m) return o.Color;
            return ResetCommitDialog.MixedColor;
        }

        List<RepoBarContextMenu.Item> BuildItems()
        {
            var items = new List<RepoBarContextMenu.Item>(options.Length);
            foreach (var opt in options)
            {
                var mode = opt.Mode;
                items.Add(new RepoBarContextMenu.Item(
                    $"{opt.Label} — {opt.Detail}",
                    () => Selected.Value = mode,
                    LabelSegments: new[]
                    {
                        new MenuLabelSegment("● ", opt.Color),
                        new MenuLabelSegment(opt.Label, Bold: true),
                        new MenuLabelSegment("  " + opt.Detail),
                    }));
            }
            return items;
        }

        return new DropdownWidget
        {
            Height = 30,
            Gap = 8,
            Children =
            [
                new Text
                {
                    Value = "●",
                    FontSize = 12,
                    Width = 14,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Prop.Bind(() => LookupColor(Selected.Value)),
                },
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = Prop.Bind<string?>(() => LookupLabel(Selected.Value)),
                    Color = Theme.Color(t => t.DialogFrame.TitleText),
                },
                // Detail fills the middle and ellipsizes rather than overflowing past the chevron.
                new Grow
                {
                    Child = new Text
                    {
                        VAlign = TextAlignment.Center,
                        Wrap = TextWrap.NoWrap,
                        Overflow = TextOverflow.Ellipsis,
                        Value = Prop.Bind<string?>(() => LookupDetail(Selected.Value)),
                        Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                    },
                },
            ],
        }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, BuildItems()));
    }
}

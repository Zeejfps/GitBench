using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
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

        var modeDropdown = new ResetModeDropdown(ctx);
        modeDropdown.BindTwoWay(modeDropdown.SelectedState, vm.Mode);

        return new Dialog
        {
            Title = "Reset to revision",
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = ("Reset", DialogButtonRole.Destructive),
            Command = vm.Reset,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = BranchName != null
                        ? $"Move the '{BranchName}' branch HEAD to the selected revision"
                        : "Move HEAD to the selected revision",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Text
                {
                    Value = BuildDirtyHint(StagedCount, UnstagedCount),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = "Branch:", Value = BranchValue(BranchName) },
                new LabeledRow { Label = "Move to:", Value = CommitValue(ShortSha, Summary) },
                new LabeledRow { Label = "Reset type:", Value = new Raw { View = modeDropdown } },
            ],
        };
    }

    private static IWidget BranchValue(string? branchName) => new Row
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
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Text
            {
                Value = branchName ?? "(detached HEAD)",
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogFrame.TitleText),
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

    private static string BuildDirtyHint(int staged, int unstaged)
    {
        var parts = new List<string>();
        if (staged > 0) parts.Add($"{staged} staged");
        if (unstaged > 0) parts.Add($"{unstaged} unstaged");
        if (parts.Count == 0) return string.Empty;
        return $"You have {string.Join(" and ", parts)} local change(s).";
    }
}

internal sealed class ResetModeDropdown : HoverableButton
{
    // Order: safest → most destructive (Fork uses Soft / Mixed / Hard top-to-bottom).
    private static readonly (ResetMode Mode, string Label, string Detail, uint Color)[] Options =
    {
        (ResetMode.Soft, "Soft", "Keep all changes. Stage differences", ResetCommitDialog.SoftColor),
        (ResetMode.Mixed, "Mixed", "Keep all changes. Unstage differences", ResetCommitDialog.MixedColor),
        (ResetMode.Hard, "Hard", "Discard all local changes", ResetCommitDialog.HardColor),
    };

    private readonly Context _ctx;

    public State<ResetMode> SelectedState { get; } = new(ResetMode.Mixed);

    public ResetMode Selected => SelectedState.Value;

    public ResetModeDropdown(Context ctx) : base(ctx)
    {
        _ctx = ctx;
        Height = 30;
        var theme = ctx.Theme();

        var dotView = new TextView(ctx.Canvas)
        {
            Text = "●",
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 14,
        };
        dotView.BindTextColor(() => LookupColor(SelectedState.Value));

        var labelView = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
        };
        labelView.BindText(() => LookupLabel(SelectedState.Value));
        labelView.BindTextColor(() => theme.Styles.Value.DialogFrame.TitleText);

        var detailView = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.NoWrap,
        };
        detailView.BindText(() => LookupDetail(SelectedState.Value));
        detailView.BindTextColor(() => theme.Styles.Value.DialogBody.RowTextMissing);

        var chevron = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        chevron.BindTextColor(() => theme.Styles.Value.DialogBody.RowText);

        // Wrapping the detail in a ClippingView keeps long descriptions from overflowing
        // past the chevron — the framework's TextView doesn't clip on its own.
        var detailClip = new ClippingView
        {
            Children = { detailView },
        };

        var row = new FlexRowView
        {
            Gap = 8,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                dotView,
                labelView,
                new FlexItem { Grow = 1, Child = detailClip },
                chevron,
            },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 4, Bottom = 4 },
            Children = { row },
        };
        BorderedButtonChrome.Bind(background, theme, IsHovered);
        SetBackground(background);
    }

    protected override void OnClicked()
    {
        var ctx = _ctx;
        var items = new List<RepoBarContextMenu.Item>(Options.Length);
        foreach (var opt in Options)
        {
            var mode = opt.Mode;
            items.Add(new RepoBarContextMenu.Item(
                $"{opt.Label} — {opt.Detail}",
                () => SelectedState.Value = mode,
                LabelSegments: new[]
                {
                    new MenuLabelSegment("● ", opt.Color),
                    new MenuLabelSegment(opt.Label, Bold: true),
                    new MenuLabelSegment("  " + opt.Detail),
                }));
        }
        RepoBarContextMenu.Show(ctx, Position.BottomLeft, items);
    }

    private static string LookupLabel(ResetMode m)
    {
        foreach (var o in Options) if (o.Mode == m) return o.Label;
        return string.Empty;
    }

    private static string LookupDetail(ResetMode m)
    {
        foreach (var o in Options) if (o.Mode == m) return o.Detail;
        return string.Empty;
    }

    private static uint LookupColor(ResetMode m)
    {
        foreach (var o in Options) if (o.Mode == m) return o.Color;
        return ResetCommitDialog.MixedColor;
    }
}

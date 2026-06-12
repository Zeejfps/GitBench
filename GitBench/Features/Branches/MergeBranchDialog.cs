using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record MergeBranchDialog : Widget
{
    public required MergeBranchRequest Request { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new MergeBranchDialogViewModel(
            Request,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var optionDropdown = new MergeOptionDropdown(ctx);
        optionDropdown.BindTwoWay(optionDropdown.SelectedState, vm.Strategy);

        return new Dialog
        {
            Title = "Merge branch",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = ("Merge", DialogButtonRole.Primary),
            Command = vm.Merge,
            ConfirmKeys = true,
            ViewModel = vm,
            FooterLead = PreviewChip(vm),
            Body =
            [
                BuildLabeledRow("Merge:", BuildBranchChip(Request.SourceDisplay)),
                BuildLabeledRow("Into:", BuildBranchChip(Request.TargetBranch)),
                BuildLabeledRow("Merge Option:", new Raw { View = optionDropdown }),
            ],
        };
    }

    private static IWidget PreviewChip(MergeBranchDialogViewModel vm)
    {
        Func<ThemeStyles, uint> color = s => vm.PreviewState.Value == MergePreviewState.Conflicts
            ? s.BranchPreview.Conflict
            : s.BranchPreview.Clean;
        return new Row
        {
            Gap = 6,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new ThemedText
                {
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 14,
                    VAlign = TextAlignment.Center,
                    Bind = () => vm.PreviewState.Value switch
                    {
                        MergePreviewState.Clean => LucideIcons.CheckSquare,
                        MergePreviewState.Conflicts => LucideIcons.CloudOff,
                        _ => string.Empty,
                    },
                    Color = color,
                },
                new ThemedText
                {
                    VAlign = TextAlignment.Center,
                    Bind = () => vm.PreviewState.Value switch
                    {
                        MergePreviewState.Clean => "Merge can be done without conflicts",
                        MergePreviewState.Conflicts => "Merge will produce conflicts",
                        _ => string.Empty,
                    },
                    Color = color,
                },
            ],
        };
    }

    private static IWidget BuildLabeledRow(string label, IWidget value) => new Row
    {
        Gap = 10,
        CrossAxis = CrossAxisAlignment.Center,
        Height = 28,
        Children =
        [
            new Row
            {
                Width = 110,
                MainAxis = MainAxisAlignment.End,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new ThemedText
                    {
                        Value = label,
                        VAlign = TextAlignment.Center,
                        Color = s => s.DialogBody.SectionHeaderText,
                    },
                ],
            },
            new Grow { Child = value },
        ],
    };

    private static IWidget BuildBranchChip(string name) => new Row
    {
        Gap = 6,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new ThemedText
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                VAlign = TextAlignment.Center,
                Color = s => s.DialogBody.BodyText,
            },
            new ThemedText
            {
                Value = name,
                VAlign = TextAlignment.Center,
                Color = s => s.DialogFrame.TitleText,
            },
        ],
    };
}

internal sealed class MergeOptionDropdown : HoverableButton
{
    private static readonly (MergeStrategy Strategy, string Label, string Detail)[] Options =
    {
        (MergeStrategy.Default, "Default", "Fast-forward if possible"),
        (MergeStrategy.NoFastForward, "Create merge commit", "Always create a merge commit"),
        (MergeStrategy.FastForwardOnly, "Fast-forward only", "Fail if not fast-forward"),
        (MergeStrategy.Squash, "Squash", "Stage changes for a new commit"),
    };

    private readonly Context _ctx;
    private readonly TextView _labelView;
    private readonly TextView _detailView;
    public State<MergeStrategy> SelectedState { get; } = new(MergeStrategy.Default);

    public MergeStrategy Selected => SelectedState.Value;

    public MergeOptionDropdown(Context ctx) : base(ctx)
    {
        _ctx = ctx;
        var theme = ctx.Theme();
        Height = 30;
        _labelView = new TextView(ctx.Canvas)
        {
            Text = LookupLabel(MergeStrategy.Default),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindTextColor(() => theme.Styles.Value.DialogFrame.TitleText);

        _detailView = new TextView(ctx.Canvas)
        {
            Text = LookupDetail(MergeStrategy.Default),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _detailView.BindTextColor(() => theme.Styles.Value.DialogBody.RowTextMissing);

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

        var row = new FlexRowView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                _labelView,
                new FlexItem { Grow = 1, Child = _detailView },
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

        this.Bind(SelectedState, s =>
        {
            _labelView.Text = LookupLabel(s);
            _detailView.Text = LookupDetail(s);
        });
    }

    protected override void OnClicked()
    {
        var items = new List<RepoBarContextMenu.Item>(Options.Length);
        foreach (var opt in Options)
        {
            var strategy = opt.Strategy;
            items.Add(new RepoBarContextMenu.Item(
                $"{opt.Label} — {opt.Detail}",
                () => SelectedState.Value = strategy));
        }
        RepoBarContextMenu.Show(_ctx, Position.BottomLeft, items);
    }

    private static string LookupLabel(MergeStrategy s)
    {
        foreach (var o in Options) if (o.Strategy == s) return o.Label;
        return string.Empty;
    }

    private static string LookupDetail(MergeStrategy s)
    {
        foreach (var o in Options) if (o.Strategy == s) return o.Detail;
        return string.Empty;
    }
}

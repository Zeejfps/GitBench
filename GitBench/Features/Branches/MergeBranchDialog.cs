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
                BuildLabeledRow("Merge Option:", new MergeOptionDropdown { Selected = vm.Strategy }),
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
                new Text
                {
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 14,
                    VAlign = TextAlignment.Center,
                    Value = vm.PreviewState.Bind(s => s switch
                    {
                        MergePreviewState.Clean => LucideIcons.CheckSquare,
                        MergePreviewState.Conflicts => LucideIcons.CloudOff,
                        _ => string.Empty,
                    }),
                    Color = Theme.Color(color),
                },
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = vm.PreviewState.Bind(s => s switch
                    {
                        MergePreviewState.Clean => "Merge can be done without conflicts",
                        MergePreviewState.Conflicts => "Merge will produce conflicts",
                        _ => string.Empty,
                    }),
                    Color = Theme.Color(color),
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
                    new Text
                    {
                        Value = label,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
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
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Text
            {
                Value = name,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogFrame.TitleText),
            },
        ],
    };
}

internal sealed record MergeOptionDropdown : Widget
{
    private static readonly (MergeStrategy Strategy, string Label, string Detail)[] Options =
    {
        (MergeStrategy.Default, "Default", "Fast-forward if possible"),
        (MergeStrategy.NoFastForward, "Create merge commit", "Always create a merge commit"),
        (MergeStrategy.FastForwardOnly, "Fast-forward only", "Fail if not fast-forward"),
        (MergeStrategy.Squash, "Squash", "Stage changes for a new commit"),
    };

    public required State<MergeStrategy> Selected { get; init; }

    protected override IWidget Build(Context ctx) => new DropdownWidget
    {
        Height = 30,
        Gap = 10,
        Children =
        [
            new Text
            {
                VAlign = TextAlignment.Center,
                Value = Prop.Bind<string?>(() => LookupLabel(Selected.Value)),
                Color = Theme.Color(s => s.DialogFrame.TitleText),
            },
            new Grow
            {
                Child = new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = Prop.Bind<string?>(() => LookupDetail(Selected.Value)),
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
            },
        ],
    }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, BuildItems()));

    private IReadOnlyList<RepoBarContextMenu.Item> BuildItems()
    {
        var items = new List<RepoBarContextMenu.Item>(Options.Length);
        foreach (var opt in Options)
        {
            var strategy = opt.Strategy;
            items.Add(new RepoBarContextMenu.Item(
                $"{opt.Label} — {opt.Detail}",
                () => Selected.Value = strategy));
        }
        return items;
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

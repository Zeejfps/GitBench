using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal readonly record struct MergeBranchRequest(
    Repo Repo,
    string SourceRef,
    string SourceDisplay,
    string TargetBranch);

internal sealed record MergeBranchDialog : Widget
{
    public required MergeBranchRequest Request { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var request = Request;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitIntegrationOperations>();
        var dispatcher = ctx.Require<IUiDispatcher>();
        var bus = ctx.Require<IMessageBus>();

        var strategy = new State<MergeStrategy>(MergeStrategy.Default);
        var previewState = new State<MergePreviewState>(MergePreviewState.Unknown);

        var merge = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => gitService.Merge(request.Repo, request.SourceRef, strategy.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                onClose();
            });

        Task.Run(() =>
        {
            MergePreviewResult result;
            try { result = gitService.PreviewMerge(request.Repo, request.SourceRef); }
            catch (Exception ex) { result = new MergePreviewResult(MergePreviewState.Unknown, ex.Message); }

            dispatcher.Post(() => previewState.Value = result.State);
        });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesMergeTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.CommonMerge, DialogButtonRole.Primary),
            Command = merge,
            ConfirmKeys = true,
            FooterLead = PreviewChip(previewState, s),
            Body =
            [
                BuildLabeledRow(s.BranchesMergeSourceLabel, BuildBranchChip(Request.SourceDisplay)),
                BuildLabeledRow(s.BranchesMergeTargetLabel, BuildBranchChip(Request.TargetBranch)),
                BuildLabeledRow(s.BranchesMergeStrategyLabel, new MergeOptionDropdown { Selected = strategy }),
            ],
        };
    }

    private static IWidget PreviewChip(IReadable<MergePreviewState> previewState, Strings s)
    {
        Func<ThemeStyles, uint> color = t => previewState.Value == MergePreviewState.Conflicts
            ? t.BranchPreview.Conflict
            : t.BranchPreview.Clean;
        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Text
                {
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = FontSize.Default,
                    VAlign = TextAlignment.Center,
                    Value = previewState.Bind(ps => ps switch
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
                    Value = previewState.Bind(ps => ps switch
                    {
                        MergePreviewState.Clean => s.BranchesMergePreviewClean,
                        MergePreviewState.Conflicts => s.BranchesMergePreviewConflicts,
                        _ => string.Empty,
                    }),
                    Color = Theme.Color(color),
                },
            ],
        };
    }

    private static IWidget BuildLabeledRow(string label, IWidget value) => new Row
    {
        Gap = Spacing.Lg,
        CrossAxis = CrossAxisAlignment.Center,
        Height = Sizes.ControlHeight,
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
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Default,
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
    public required State<MergeStrategy> Selected { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        (MergeStrategy Strategy, string Label, string Detail)[] options =
        {
            (MergeStrategy.Default, s.BranchesMergeStrategyDefault, s.BranchesMergeStrategyDefaultDetail),
            (MergeStrategy.NoFastForward, s.BranchesMergeStrategyNoFf, s.BranchesMergeStrategyNoFfDetail),
            (MergeStrategy.FastForwardOnly, s.BranchesMergeStrategyFfOnly, s.BranchesMergeStrategyFfOnlyDetail),
            (MergeStrategy.Squash, s.BranchesMergeStrategySquash, s.BranchesMergeStrategySquashDetail),
        };

        string LookupLabel(MergeStrategy strategy)
        {
            foreach (var o in options) if (o.Strategy == strategy) return o.Label;
            return string.Empty;
        }

        string LookupDetail(MergeStrategy strategy)
        {
            foreach (var o in options) if (o.Strategy == strategy) return o.Detail;
            return string.Empty;
        }

        List<RepoBarContextMenu.Item> BuildItems()
        {
            var items = new List<RepoBarContextMenu.Item>(options.Length);
            foreach (var opt in options)
            {
                var strategy = opt.Strategy;
                items.Add(new RepoBarContextMenu.Item(
                    $"{opt.Label} — {opt.Detail}",
                    () => Selected.Value = strategy));
            }
            return items;
        }

        return new DropdownWidget
        {
            Height = 30,
            Gap = Spacing.Lg,
            Children =
            [
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = Prop.Bind<string?>(() => LookupLabel(Selected.Value)),
                    Color = Theme.Color(t => t.DialogFrame.TitleText),
                },
                new Grow
                {
                    Child = new Text
                    {
                        VAlign = TextAlignment.Center,
                        Value = Prop.Bind<string?>(() => LookupDetail(Selected.Value)),
                        Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                    },
                },
            ],
        }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, BuildItems()));
    }
}

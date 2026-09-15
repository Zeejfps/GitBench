using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal readonly record struct RebaseBranchRequest(
    Repo Repo,
    string SourceBranch,
    string TargetRef,
    string TargetDisplay);

internal sealed record RebaseBranchDialog : Widget
{
    public required RebaseBranchRequest Request { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var request = Request;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitIntegrationOperations>();
        var dispatcher = ctx.Require<IUiDispatcher>();
        var bus = ctx.Require<IMessageBus>();

        var autostash = new State<bool>(false);
        var previewState = new State<RebasePreviewState>(RebasePreviewState.Unknown);

        var rebase = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => gitService.Rebase(request.Repo, request.TargetRef, autostash.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                onClose();
            });

        Task.Run(() =>
        {
            RebasePreviewResult result;
            try { result = gitService.PreviewRebase(request.Repo, request.TargetRef); }
            catch (Exception ex) { result = new RebasePreviewResult(RebasePreviewState.Unknown, ex.Message); }

            dispatcher.Post(() => previewState.Value = result.State);
        });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesRebaseTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.CommonRebase, DialogButtonRole.Primary),
            Command = rebase,
            ConfirmKeys = true,
            FooterLead = PreviewChip(previewState, s),
            Body =
            [
                new Text
                {
                    Value = s.BranchesRebaseDescription,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                BuildLabeledRow(s.BranchesRebaseSourceLabel, BuildBranchChip(Request.SourceBranch)),
                BuildLabeledRow(s.BranchesRebaseTargetLabel, BuildBranchChip(Request.TargetDisplay)),
                BuildLabeledRow("", new CheckboxWidget
                {
                    Label = s.BranchesRebaseAutostashLabel,
                    Checked = autostash,
                    Height = 24,
                }.WithController<KbmController>()),
            ],
        };
    }

    private static IWidget PreviewChip(IReadable<RebasePreviewState> previewState, Strings s)
    {
        Func<ThemeStyles, uint> color = t => previewState.Value == RebasePreviewState.Conflicts
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
                        RebasePreviewState.Clean => LucideIcons.CheckSquare,
                        RebasePreviewState.Conflicts => LucideIcons.CloudOff,
                        _ => string.Empty,
                    }),
                    Color = Theme.Color(color),
                },
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = previewState.Bind(ps => ps switch
                    {
                        RebasePreviewState.Clean => s.BranchesRebasePreviewClean,
                        RebasePreviewState.Conflicts => s.BranchesRebasePreviewConflicts,
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

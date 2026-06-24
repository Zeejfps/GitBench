using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
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

internal sealed record RebaseBranchDialog : Widget
{
    public required RebaseBranchRequest Request { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new RebaseBranchDialogViewModel(
            Request,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesRebaseTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = (s.CommonRebase, DialogButtonRole.Primary),
            Command = vm.Rebase,
            ConfirmKeys = true,
            FooterLead = PreviewChip(vm, s),
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
                    Checked = vm.Autostash,
                    Height = 24,
                }.WithController<KbmController>()),
            ],
        };
    }

    private static IWidget PreviewChip(RebaseBranchDialogViewModel vm, Strings s)
    {
        Func<ThemeStyles, uint> color = t => vm.PreviewState.Value == RebasePreviewState.Conflicts
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
                    Value = vm.PreviewState.Bind(ps => ps switch
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
                    Value = vm.PreviewState.Bind(ps => ps switch
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

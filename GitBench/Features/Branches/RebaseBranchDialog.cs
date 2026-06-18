using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
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

        return new Dialog
        {
            Title = "Rebase",
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = ("Rebase", DialogButtonRole.Primary),
            Command = vm.Rebase,
            ConfirmKeys = true,
            FooterLead = PreviewChip(vm),
            Body =
            [
                new Text
                {
                    Value = "Copy commits from one branch to another",
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
                BuildLabeledRow("Rebase:", BuildBranchChip(Request.SourceBranch)),
                BuildLabeledRow("On:", BuildBranchChip(Request.TargetDisplay)),
                BuildLabeledRow("", new Checkbox
                {
                    Label = "Stash and reapply local changes",
                    Checked = vm.Autostash,
                    Height = 24,
                }.WithController<KbmController>()),
            ],
        };
    }

    private static IWidget PreviewChip(RebaseBranchDialogViewModel vm)
    {
        Func<ThemeStyles, uint> color = s => vm.PreviewState.Value == RebasePreviewState.Conflicts
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
                        RebasePreviewState.Clean => LucideIcons.CheckSquare,
                        RebasePreviewState.Conflicts => LucideIcons.CloudOff,
                        _ => string.Empty,
                    }),
                    Color = Theme.Color(color),
                },
                new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = vm.PreviewState.Bind(s => s switch
                    {
                        RebasePreviewState.Clean => "Rebase can be done without conflicts",
                        RebasePreviewState.Conflicts => "Rebase will produce conflicts",
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

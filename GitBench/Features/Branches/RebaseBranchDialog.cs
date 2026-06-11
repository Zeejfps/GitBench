using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record RebaseBranchDialog : Widget
{
    public required RebaseBranchRequest Request { get; init; }
    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = new RebaseBranchDialogViewModel(
            Request,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var view = new Dialog
        {
            Title = "Rebase",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = ("Rebase", DialogButtonRole.Primary),
            Command = vm.Rebase,
            ConfirmKeys = true,
            FooterLead = PreviewChip(vm),
            Body =
            [
                new ThemedText
                {
                    Value = "Copy commits from one branch to another",
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = s => s.DialogBody.RowTextMissing,
                },
                BuildLabeledRow("Rebase:", BuildBranchChip(Request.SourceBranch)),
                BuildLabeledRow("On:", BuildBranchChip(Request.TargetDisplay)),
                BuildLabeledRow("", new Checkbox
                {
                    Label = "Stash and reapply local changes",
                    Value = vm.Autostash,
                    Height = 24,
                }),
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
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
                new ThemedText
                {
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 14,
                    VAlign = TextAlignment.Center,
                    Bind = () => vm.PreviewState.Value switch
                    {
                        RebasePreviewState.Clean => LucideIcons.CheckSquare,
                        RebasePreviewState.Conflicts => LucideIcons.CloudOff,
                        _ => string.Empty,
                    },
                    Color = color,
                },
                new ThemedText
                {
                    VAlign = TextAlignment.Center,
                    Bind = () => vm.PreviewState.Value switch
                    {
                        RebasePreviewState.Clean => "Rebase can be done without conflicts",
                        RebasePreviewState.Conflicts => "Rebase will produce conflicts",
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

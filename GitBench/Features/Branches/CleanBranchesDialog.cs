using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Operations;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Confirmation modal for cleaning up stale local branches under a folder. Offers a checkbox
/// per cleanup category — disconnected (upstream deleted) and never-pushed (no upstream) — and
/// previews the exact branches the current selection targets so the destructive delete is never
/// blind. "Delete even if not fully merged" maps to git's force flag; with it off, unmerged
/// branches are skipped rather than removed.
/// </summary>
internal sealed record CleanBranchesDialog : Widget
{
    public required Repo Repo { get; init; }

    /// The folder the cleanup is scoped to; empty for the "Local" root. Shown to the user so the
    /// sub-folder scoping is visible.
    public required string FolderPath { get; init; }

    public required IReadOnlyList<CleanBranchCandidate> Candidates { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CleanBranchesDialogViewModel(
            Repo,
            Candidates,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        var body = new List<IWidget>
        {
            new Text
            {
                Value = s.BranchesCleanDescription,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.BodyText),
            },
        };

        if (FolderPath.Length > 0)
        {
            body.Add(new Text
            {
                Value = s.BranchesCleanScope(FolderPath),
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            });
        }

        if (vm.DisconnectedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanDisconnectedLabel(vm.DisconnectedCount),
                Checked = vm.CleanDisconnected,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        if (vm.NeverPushedCount > 0)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesCleanNeverPushedLabel(vm.NeverPushedCount),
                Checked = vm.CleanNeverPushed,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        body.Add(new CheckboxWidget
        {
            Label = s.BranchesCleanForceLabel,
            Checked = vm.Force,
            Height = Sizes.RowHeight,
        }.WithController<KbmController>());
        body.Add(new Text
        {
            Value = s.BranchesCleanForceHint,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
        });

        body.Add(new Text
        {
            Value = Prop.Bind(vm.SelectedHeader),
            Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
        });
        body.Add(new Grow { Child = new Raw { View = BuildPreview(ctx, vm) } });

        return new Dialog
        {
            Title = s.BranchesCleanTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Height = 460f,
            BodyGap = 10,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            BindActionLabel = vm.ActionLabel,
            Command = vm.Clean,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }

    private static View BuildPreview(Context ctx, CleanBranchesDialogViewModel vm)
    {
        var theme = ctx.Theme();

        var column = new Column<string>
        {
            Gap = Spacing.Hair,
            Items = Prop.Bind(vm.SelectedNames),
            Template = name => new Text
            {
                Value = name,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(t => t.FileChangeRow.RowText),
            },
        }.BuildView(ctx);

        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(column);
        scrollPane.UseController(ctx.Require<InputSystem>(),
            () => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var host = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(Spacing.Sm),
                    Children =
                    {
                        new BorderLayoutView
                        {
                            Center = scrollPane,
                            East = vScrollBar,
                        },
                    },
                },
            },
        };
        host.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        host.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));
        host.Use(() => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        return host;
    }
}

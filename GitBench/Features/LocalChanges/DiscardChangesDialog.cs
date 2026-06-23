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

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Confirmation modal for discarding unstaged changes. Lists every unstaged path with a
/// checkbox — the paths the user had selected when they invoked Discard come pre-checked —
/// so they can fine-tune the set before committing to the throw-away. Discard is a
/// destructive action: the worktree changes (and any untracked files in the set) cannot
/// be recovered from git afterwards.
/// </summary>
internal sealed record DiscardChangesDialog : Widget
{
    public required Repo Repo { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DiscardChangesViewModel(
            new DiscardChangesRequest(Repo, Paths),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = s.LocalchangesDiscardDialogTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Height = 480f,
            BodyGap = 10,
            Action = ("Discard", DialogButtonRole.Destructive),
            Command = vm.Discard,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.LocalchangesDiscardDialogBody,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = Prop.Bind(vm.FilesHeader),
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                new Grow { Child = new Raw { View = BuildFileList(ctx, vm) } },
            ],
        };
    }

    private static View BuildFileList(Context ctx, DiscardChangesViewModel vm)
    {
        var theme = ctx.Theme();
        var column = new ColumnView { Gap = 1 };

        var files = vm.Files.Value;
        if (files.Count == 0)
        {
            var empty = new TextView(ctx.Canvas)
            {
                Text = ctx.Localization().Strings.Value.LocalchangesDiscardDialogNoChanges,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            empty.BindTextColor(() => theme.Styles.Value.FileChangesSection.EmptyPlaceholderText);
            column.Children.Add(empty);
        }
        else
        {
            foreach (var file in files)
                column.Children.Add(BuildRow(ctx, vm, file));
        }

        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(column);
        scrollPane.UseController(ctx.Require<InputSystem>(),
            () => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var fileScrollHost = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(6),
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
        fileScrollHost.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        fileScrollHost.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));
        fileScrollHost.Use(() => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        return fileScrollHost;
    }

    private static View BuildRow(Context ctx, DiscardChangesViewModel vm, DiscardFileRow file)
    {
        var theme = ctx.Theme();
        var badge = new FileStatusBadge { Status = file.Display.Status }.BuildView(ctx);

        var pathText = new TextView(ctx.Canvas)
        {
            Text = FileChangeFormatting.FormatPath(file.Display),
            VerticalTextAlignment = TextAlignment.Center,
        };
        pathText.BindTextColor(() => theme.Styles.Value.FileChangeRow.RowText);

        var rowContent = new FlexRowView
        {
            Gap = 8f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                badge,
                new FlexItem { Grow = 1, Child = pathText },
            },
        };

        // Seed from VM state BEFORE wiring Changed, so the initial paint doesn't trigger
        // a phantom toggle through the handler.
        var isChecked = new State<bool>(vm.CheckedPaths.Value.Contains(file.Path));
        isChecked.Changed += _ => vm.ToggleFile(file.Path);

        return new CheckboxWidget
        {
            Content = new Raw { View = rowContent },
            Checked = isChecked,
            Height = 22,
        }.WithController<KbmController>().BuildView(ctx);
    }
}

public readonly record struct DiscardChangesRequest(Repo Repo, IReadOnlyList<string> Paths);

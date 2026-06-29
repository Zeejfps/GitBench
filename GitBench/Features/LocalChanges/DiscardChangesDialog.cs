using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
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
    // Rows the file list shows before it scrolls internally. Past this the card stops growing and
    // its own scrollbar takes over, so a huge change set can't stretch the dialog off-screen.
    private const int MaxVisibleRows = 11;

    public required Repo Repo { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DiscardChangesViewModel(
            new DiscardChangesRequest(Repo, Paths),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = s.LocalchangesDiscardDialogTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            BodyGap = 10,
            Action = (s.CommonDiscard, DialogButtonRole.Destructive),
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
                new Raw { View = BuildFileList(ctx, vm) },
            ],
        };
    }

    private static View BuildFileList(Context ctx, DiscardChangesViewModel vm)
    {
        var theme = ctx.Theme();
        var column = new ColumnView { Gap = Spacing.Hair };

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

        // The dialog frame already wraps the body in a scroll region that lays its content out at
        // the content's natural height, so a Grow can't bound this list — it would inflate the whole
        // body and hand scrolling to the frame's outer bar. An explicit height (honored at measure
        // time, unlike MaxHeightConstraint) caps the card to MaxVisibleRows and lets it scroll
        // internally beyond that, while hugging its rows when there are fewer.
        var visibleRows = Math.Min(Math.Max(files.Count, 1), MaxVisibleRows);
        var listHeight = visibleRows * Sizes.RowHeight + (visibleRows - 1) * Spacing.Hair;

        var fileScrollHost = new RectView
        {
            Height = listHeight + 2 * Spacing.Sm + 2f,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(Spacing.Sm),
                    Children =
                    {
                        new DialogScrollRegion { Content = new Raw { View = column } }.BuildView(ctx),
                    },
                },
            },
        };
        fileScrollHost.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        fileScrollHost.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

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
            TextOverflow = TextOverflow.Ellipsis,
        };
        pathText.BindTextColor(() => theme.Styles.Value.FileChangeRow.RowText);

        var rowContent = new FlexRowView
        {
            Gap = Spacing.Md,
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
            Height = Sizes.RowHeight,
        }.WithController<KbmController>().BuildView(ctx);
    }
}

public readonly record struct DiscardChangesRequest(Repo Repo, IReadOnlyList<string> Paths);

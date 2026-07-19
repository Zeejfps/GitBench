using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Modal shown when a git operation fails. Surfaces git's stderr block verbatim (so it
/// matches what the user would see running the command in a terminal) inside a vertically
/// scrollable monospace body — long blocks like "your local changes to the following
/// files would be overwritten by merge" plus a file list stay fully readable instead of
/// being clipped to the first line.
///
/// The body wraps at the dialog's content width and only scrolls vertically; that keeps
/// long lines visible without letting wide content stretch the dialog horizontally.
/// </summary>
internal sealed record OperationErrorDialog : Widget<DialogState>
{
    private const float CloseButtonSize = 28f;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public required Action OnClose { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    // Null unless git's output names a stale *.lock; drives the recovery button and clears once
    // the file is gone, so the dialog can't offer to delete the same path twice.
    private readonly State<string?> _lockPath = new(null);
    private readonly State<string?> _lockStatus = new(null);
    private string _lockRemovedText = string.Empty;

    protected override IWidget Build(Context ctx, DialogState state)
    {
        var s = ctx.Localization().Strings.Value;
        _lockPath.Value = GitLockFile.Detect(Message);
        _lockRemovedText = s.OperationsLockRemoved;
        return new Box
        {
            Width = DialogFrame.WidthWide,
            Height = 360,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(t => t.DialogFrame.Background),
            BorderColor = Theme.BorderColor(t => BorderColorStyle.All(t.DialogFrame.Border)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                BuildHeader(s),
                                new Box { Height = 1, Background = Theme.Color(t => t.DialogFrame.HeaderSeparator) },
                                new Grow { Child = new Raw { View = BuildScrollHost(ctx) } },
                                new Row
                                {
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Gap = Spacing.Sm,
                                    Children =
                                    [
                                        new SecondaryDialogButton
                                        {
                                            Label = s.OperationsLockRemove,
                                            Command = new Command(RemoveLockFile),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                            Visible = Prop.Bind(() => _lockPath.Value != null),
                                        }.WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = Prop.Bind(() => _lockStatus.Value ?? string.Empty),
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                                            Visible = Prop.Bind(() => _lockStatus.Value != null),
                                        },
                                        new Spacer(),
                                        new ActionDialogButton
                                        {
                                            Label = s.CommonOk,
                                            Role = DialogButtonRole.Primary,
                                            Command = new Command(OnClose),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                        }.WithController<KbmController>(),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private void RemoveLockFile()
    {
        if (_lockPath.Value is not { } path) return;

        var failure = GitLockFile.Remove(path);
        _lockStatus.Value = failure ?? _lockRemovedText;
        if (failure is null) _lockPath.Value = null;
    }

    // Symmetric left spacer keeps the title centered: it matches the combined width of the two
    // right-side buttons (close + copy), so the grown, center-aligned title lands in the middle.
    private IWidget BuildHeader(Strings s) => new Row
    {
        Height = Sizes.ControlHeight,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Box { Width = CloseButtonSize * 2 },
            new Grow
            {
                Child = new Text
                {
                    Value = Title,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogFrame.TitleText),
                },
            },
            new DialogCopyButton { GetText = () => Message, Tooltip = s.OperationsErrorCopyTooltip },
            new DialogCloseButton { OnClose = OnClose },
        ],
    };

    // The error body scrolls vertically only. VerticalScrollPane forces its child to viewport
    // width — that gives the wrapping TextView the bound it needs to wrap instead of measuring its
    // own one-line natural width and stretching the layout horizontally. A two-axis ScrollPane would
    // let one long line balloon past the dialog edges. The scroll plumbing has no widget form, so it
    // rides in as a raw view.
    private View BuildScrollHost(Context ctx)
    {
        var theme = ctx.Theme();

        var messageView = new Text
        {
            Value = Message,
            FontFamily = DiffOptions.MonoFontFamily,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(s => s.DialogBody.BodyText),
        }.BuildView(ctx);

        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(new PaddingView
        {
            Padding = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = Spacing.Sm, Bottom = Spacing.Sm },
            Children = { messageView },
        });
        scrollPane.UseController(ctx.Require<InputSystem>(),
            () => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var scrollHost = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.ControlBorderRadius),
            Children =
            {
                new BorderLayoutView
                {
                    Center = scrollPane,
                    East = vScrollBar,
                },
            },
        };
        scrollHost.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        scrollHost.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));
        scrollHost.Use(() => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        return scrollHost;
    }
}

internal sealed class VerticalScrollPaneWheelController : KeyboardMouseController
{
    private readonly VerticalScrollPane _pane;

    public VerticalScrollPaneWheelController(VerticalScrollPane pane)
    {
        _pane = pane;
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        // Consume only if we actually moved. The wheel dispatches root→leaf in the capture phase,
        // so an outer pane that can't scroll (e.g. a dialog body that fits) must let the event reach
        // the inner list instead of swallowing it.
        if (_pane.Scroll(-e.DeltaY * Scrolling.WheelStep))
            e.Consume();
    }
}

internal sealed class VerticalScrollBarSyncController : IDisposable
{
    private readonly VerticalScrollPane _pane;
    private readonly VerticalScrollBarView _bar;

    public VerticalScrollBarSyncController(VerticalScrollPane pane, VerticalScrollBarView bar)
    {
        _pane = pane;
        _bar = bar;
        _pane.ScrollPositionChanged += OnPaneScrolled;
        _bar.ScrollPositionChanged += OnBarMoved;
    }

    public void Dispose()
    {
        _pane.ScrollPositionChanged -= OnPaneScrolled;
        _bar.ScrollPositionChanged -= OnBarMoved;
    }

    private void OnPaneScrolled(float normalized)
        => ScrollBarSync.ApplyVertical(_bar, _pane.Scale, normalized);

    private void OnBarMoved(float normalized)
        => _pane.SetNormalizedScrollPosition(normalized, notify: false);
}

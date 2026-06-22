using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

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
internal sealed record OperationErrorDialog : Widget
{
    private const float CloseButtonSize = 28f;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();

        var titleView = new Text
        {
            Value = Title,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.DialogFrame.TitleText),
        }.BuildView(ctx);

        var copyButton = new DialogCopyButton
        {
            GetText = () => Message,
            Tooltip = "Copy error to clipboard",
        }.BuildView(ctx);

        // Symmetric left spacer keeps the title centered: matches the combined width of the
        // two right-side buttons plus the row's gap (28 + 28 + Gap). With Gap=0 here, the
        // spacer width = sum of buttons.
        var headerRow = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                new ContainerView { Width = CloseButtonSize * 2 },
                new FlexItem { Grow = 1, Child = titleView },
                copyButton,
                new DialogCloseButton { OnClose = OnClose }.BuildView(ctx),
            },
        };

        var messageView = new Text
        {
            Value = Message,
            FontFamily = DiffOptions.MonoFontFamily,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(s => s.DialogBody.BodyText),
        }.BuildView(ctx);

        // VerticalScrollPane forces its child to viewport width — that gives the wrapping
        // TextView the bound it needs to wrap instead of measuring its own one-line natural
        // width and stretching the layout horizontally. ScrollPane (two-axis) lets the
        // child grow to its natural width, which would let one long line balloon past the
        // dialog edges.
        var scrollPane = new VerticalScrollPane();
        scrollPane.Children.Add(new PaddingView
        {
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 6, Bottom = 6 },
            Children = { messageView },
        });
        scrollPane.UseController(ctx.Require<InputSystem>(),
            () => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var scrollHost = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(4),
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

        var okButton = new DialogButton(ctx, "OK", OnClose, DialogButtonRole.Primary)
        {
            Height = DialogFrame.DefaultButtonHeight,
            MinWidthConstraint = DialogFrame.DefaultButtonMinWidth,
        };

        var buttonsRow = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = new ContainerView() },
                okButton,
            },
        };

        var separator = new RectView { Height = 1 };
        separator.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.HeaderSeparator);

        var frame = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(10),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(20),
                    Children =
                    {
                        new FlexColumnView
                        {
                            Gap = 12,
                            CrossAxisAlignment = CrossAxisAlignment.Stretch,
                            Children =
                            {
                                headerRow,
                                separator,
                                new FlexItem { Grow = 1, Child = scrollHost },
                                buttonsRow,
                            },
                        },
                    },
                },
            },
        };
        frame.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.Background);
        frame.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

        var root = new ContainerView
        {
            Width = DialogFrame.WidthWide,
            Height = 360,
        };
        root.Children.Add(frame);
        root.UseController(ctx.Require<InputSystem>(), () => new DialogKbmController(OnClose));
        return root;
    }
}

internal sealed class VerticalScrollPaneWheelController : KeyboardMouseController
{
    private const float Step = 60f;
    private readonly VerticalScrollPane _pane;

    public VerticalScrollPaneWheelController(VerticalScrollPane pane)
    {
        _pane = pane;
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        _pane.Scroll(-e.DeltaY * Step);
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

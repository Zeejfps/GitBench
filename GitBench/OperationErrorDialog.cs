using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;

namespace GitGui;

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
public sealed class OperationErrorDialog : MultiChildView
{
    private const float CloseButtonSize = 28f;

    public OperationErrorDialog(string title, string message, Action onClose)
    {
        Width = 560;
        Height = 360;

        var titleView = new TextView
        {
            Text = title,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        titleView.BindThemedTextColor(s => s.DialogFrame.TitleText);

        // Pull IClipboard lazily off the context — the dialog is constructed before it's
        // attached, so capturing it in a closure that runs on click is the simplest path.
        var copyButton = new DialogCopyButton(
            () => Context?.Get<IClipboard>()?.SetText(message),
            tooltip: "Copy error to clipboard");

        // Symmetric left spacer keeps the title centered: matches the combined width of the
        // two right-side buttons plus the row's gap (28 + 28 + Gap). With Gap=0 here, the
        // spacer width = sum of buttons.
        var headerRow = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                new MultiChildView { Width = CloseButtonSize * 2 },
                new FlexItem { Grow = 1, Child = titleView },
                copyButton,
                new DialogCloseButton(onClose),
            },
        };

        var messageView = new TextView
        {
            Text = message,
            FontFamily = DiffOptions.MonoFontFamily,
            TextWrap = TextWrap.Wrap,
        };
        messageView.BindThemedTextColor(s => s.DialogBody.BodyText);

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
        scrollPane.UseController(_ => new VerticalScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical();

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
        scrollHost.BindThemedBackgroundColor(s => s.DialogFrame.InsetBackground);
        scrollHost.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));
        scrollHost.UsePresenter(_ => new VerticalScrollBarSyncController(scrollPane, vScrollBar));

        var okButton = new DialogButton("OK", onClose)
        {
            Height = 32,
        };

        var buttonsRow = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = new MultiChildView() },
                new FlexItem { Grow = 1, Child = okButton },
            },
        };

        var separator = new RectView { Height = 1 };
        separator.BindThemedBackgroundColor(s => s.DialogFrame.HeaderSeparator);

        var frame = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(10),
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
        };
        frame.BindThemedBackgroundColor(s => s.DialogFrame.Background);
        frame.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));
        AddChildToSelf(frame);

        this.UseController(_ => new DialogKbmController(onClose));
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

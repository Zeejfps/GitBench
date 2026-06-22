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
internal sealed record OperationErrorDialog : Widget
{
    private const float CloseButtonSize = 28f;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();

        return new Box
        {
            Width = DialogFrame.WidthWide,
            Height = 360,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(s => s.DialogFrame.Background),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.DialogFrame.Border)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = 12,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                BuildHeader(),
                                new Box { Height = 1, Background = Theme.Color(s => s.DialogFrame.HeaderSeparator) },
                                new Grow { Child = new Raw { View = BuildScrollHost(ctx) } },
                                new Row
                                {
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new Spacer(),
                                        new DialogButtonWidget
                                        {
                                            Label = "OK",
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
        }.WithController(input, () => new DialogKbmController(OnClose));
    }

    // Symmetric left spacer keeps the title centered: it matches the combined width of the two
    // right-side buttons (close + copy), so the grown, center-aligned title lands in the middle.
    private IWidget BuildHeader() => new Row
    {
        Height = 28,
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
                    Color = Theme.Color(s => s.DialogFrame.TitleText),
                },
            },
            new DialogCopyButton { GetText = () => Message, Tooltip = "Copy error to clipboard" },
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
            Padding = new PaddingStyle { Left = 8, Right = 8, Top = 6, Bottom = 6 },
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

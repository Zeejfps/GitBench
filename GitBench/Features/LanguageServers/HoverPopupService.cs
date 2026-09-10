using GitBench.Features.Operations;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Widgets;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LanguageServers;

internal sealed class HoverPopupService : IHoverPresenter, IDisposable
{
    public const int Gap = 8;

    public const float CardWidth = HoverCard.WidthPx;

    public const float CardMaxHeight = HoverCard.MaxHeightPx;

    private readonly IPopupWindowFactory _factory;
    private readonly IWindowCoordinates _coordinates;

    private object? _owner;
    private IPopupWindow? _popup;

    public HoverPopupService(IPopupWindowFactory factory, IWindowCoordinates coordinates)
    {
        _factory = factory;
        _coordinates = coordinates;
    }

    public void Show(object owner, HoverText hover, RectF anchorCanvas)
    {
        Release();

        var rendered = MarkdownFile.Render(hover.Markdown);

        var anchor = _coordinates.ToScreenPoints(CanvasRect.From(anchorCanvas));
        _owner = owner;
        _popup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx => Direction.Wrap(new HoverCard { Render = rendered }).BuildView(ctx),
            Place = size =>
            {
                var preferred = new ScreenRect(anchor.X, anchor.Y + anchor.Height + Gap, size.Width, size.Height);
                var flipped = new ScreenRect(anchor.X, anchor.Y - Gap - size.Height, size.Width, size.Height);
                return (preferred, flipped);
            },
            MousePassThrough = true,
        });
    }

    public void Hide(object owner)
    {
        if (ReferenceEquals(_owner, owner)) Release();
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_popup is null) return;
        _factory.Release(_popup);
        _popup = null;
        _owner = null;
    }
}

internal sealed record HoverCard : Widget
{
    internal const float WidthPx = 520f;

    internal const float MaxHeightPx = 420f;

    public required MarkdownRender Render { get; init; }

    private View Scroller(Context ctx)
    {
        var pane = new VerticalScrollPane { FillParent = false, StretchContent = false };
        pane.Children.Add(new MarkdownWidget { Document = Render.Document }.BuildView(ctx));
        pane.UseController(ctx.Require<InputSystem>(), () => new VerticalScrollPaneWheelController(pane));
        return pane;
    }

    protected override IWidget Build(Context ctx) => new Box
    {
        Width = WidthPx,
        MaxHeight = MaxHeightPx,
        Background = Theme.Color(s => s.Tooltip.Background),
        BorderRadius = BorderRadiusStyle.All(Radius.Md),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Tooltip.Border)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 10, Top = 8, Right = 10, Bottom = 8 },
                Children = [new Raw { View = Scroller(ctx) }],
            },
        ],
    };
}


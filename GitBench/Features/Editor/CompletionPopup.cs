using GitBench.Controls;
using GitBench.Features.CodeIntel;
using GitBench.Features.FileBrowser;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Editor;

/// <summary>
/// Shows an open completion list under the identifier it completes. The popup never takes the
/// keyboard or the pointer: the editor keeps typing, and steers the list from its own keys.
/// </summary>
internal sealed class CompletionPopup : IDisposable
{
    private const int Gap = 2;

    private readonly IPopupWindowFactory _factory;
    private readonly IWindowCoordinates _coordinates;

    private IPopupWindow? _popup;
    private CompletionListView? _view;
    private ScreenRect _anchor;

    public CompletionPopup(IPopupWindowFactory factory, IWindowCoordinates coordinates)
    {
        _factory = factory;
        _coordinates = coordinates;
    }

    /// <summary>Shows a list, replacing whatever list was shown. <paramref name="wordStart"/> is the
    /// canvas rect of a caret standing where the identifier begins.</summary>
    public void Show(CompletionList list, RectF wordStart)
    {
        var shifted = new RectF(
            wordStart.Left - CompletionListView.LabelInset, wordStart.Bottom, wordStart.Width, wordStart.Height);
        var anchor = _coordinates.ToScreenPoints(CanvasRect.From(shifted));

        // A new window shows before it has painted, so a list that still fits the window it has —
        // a moved selection, a narrowed list — is repainted in place rather than reopened.
        if (_view is not null && anchor == _anchor && _view.TryShow(list)) return;

        var previous = _popup;
        var minWidth = anchor == _anchor && _view is not null ? _view.SizedWidth : 0f;
        _anchor = anchor;
        _popup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx =>
            {
                _view = new CompletionListView(ctx.Canvas, list, ctx.Theme().Styles.Value, minWidth);
                return Direction.Wrap(new Raw { View = _view }).BuildView(ctx);
            },
            Place = size =>
            {
                var below = new ScreenRect(anchor.X, anchor.Y + anchor.Height + Gap, size.Width, size.Height);
                var above = new ScreenRect(anchor.X, anchor.Y - Gap - size.Height, size.Width, size.Height);
                return (below, above);
            },
            MousePassThrough = true,
        });
        if (previous is not null) _factory.Release(previous);
    }

    public void Hide()
    {
        if (_popup is null) return;
        _factory.Release(_popup);
        _popup = null;
        _view = null;
    }

    public void Dispose() => Hide();
}

/// <summary>
/// The rows of a completion list, painted: a kind glyph, the label with the characters the prefix
/// matched drawn emphasized, and the selected row filled. A window of
/// <see cref="CompletionSession.VisibleRows"/> rows kept around the selection.
/// </summary>
internal sealed class CompletionListView : View
{
    public const float RowHeight = 22f;

    private const float Pad = 4f;
    private const float IconColumn = 20f;
    private const float MinWidth = 220f;
    private const float MaxWidth = 640f;
    private const float DetailGap = 24f;
    private const float MaxDetailWidth = 260f;

    /// <summary>How far right of the popup's edge a label starts, so the popup can be placed with
    /// its labels under the word they complete.</summary>
    public const float LabelInset = Pad + IconColumn + 1f;

    private readonly ICanvas _canvas;
    private readonly ThemeStyles _styles;
    private CompletionList _list;
    private int _top;
    private int _count;
    private readonly float _width;
    private readonly float _height;

    private readonly TextStyle _icon = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FileGlyph.LucideSize,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private readonly TextStyle _plain = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };

    private readonly TextStyle _matched = new()
    {
        FontFamily = MonoFonts.Bold,
        FontSize = FontSize.Body,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };

    private readonly TextStyle _detail = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.End,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };

    /// <param name="minWidth">The width of the list this one replaces, so narrowing a list as its
    /// prefix grows does not narrow the popup with it.</param>
    public CompletionListView(ICanvas canvas, CompletionList list, ThemeStyles styles, float minWidth)
    {
        _canvas = canvas;
        _styles = styles;
        _list = list;
        _count = Rows(list);
        _top = TopFor(list, _count, 0);
        _width = MathF.Max(minWidth, WidthFor(list));
        _height = HeightFor(_count);
        Width = _width;
        Height = _height;
    }

    /// <summary>Draws another list in this one's place, as long as it fits the window already
    /// sized for this one. Returns false when it would not, and the popup has to be reopened.</summary>
    public bool TryShow(CompletionList list)
    {
        var count = Rows(list);
        if (HeightFor(count) != _height || WidthFor(list) > _width) return false;

        _top = ReferenceEquals(list.Items, _list.Items) ? TopFor(list, count, _top) : TopFor(list, count, 0);
        _list = list;
        _count = count;
        SetDirty();
        return true;
    }

    /// <summary>The width this list was sized to, which a list replacing it keeps as a floor.</summary>
    public float SizedWidth => _width;

    private static int Rows(CompletionList list) => Math.Min(list.Items.Count, CompletionSession.VisibleRows);

    /// <summary>The first row shown: unchanged while the selection stays in view, otherwise scrolled
    /// just far enough to bring it back.</summary>
    private static int TopFor(CompletionList list, int count, int top)
    {
        if (list.Selected < top) top = list.Selected;
        else if (list.Selected >= top + count) top = list.Selected - count + 1;
        return Math.Clamp(top, 0, list.Items.Count - count);
    }

    private static float HeightFor(int count) => count * RowHeight + Pad * 2;

    private float WidthFor(CompletionList list)
    {
        var widest = 0f;
        var widestDetail = 0f;
        foreach (var ranked in list.Items)
        {
            widest = MathF.Max(widest, _canvas.MeasureTextWidth(ranked.Item.Label, _plain));
            if (ranked.Item.Detail is { Length: > 0 } detail)
                widestDetail = MathF.Max(widestDetail, _canvas.MeasureTextWidth(detail, _detail));
        }

        var details = widestDetail > 0 ? DetailGap + MathF.Min(widestDetail, MaxDetailWidth) : 0f;
        return Math.Clamp(LabelInset + widest + details + Pad * 3, MinWidth, MaxWidth);
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();
        var menu = _styles.ContextMenu;

        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = new RectStyle
            {
                BackgroundColor = menu.Background,
                BorderColor = BorderColorStyle.All(menu.Border),
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(Radius.Md),
            },
            ZIndex = z,
        });

        for (var i = 0; i < _count; i++)
        {
            var index = _top + i;
            var row = new RectF(pos.Left + Pad, pos.Top - Pad - (i + 1) * RowHeight, pos.Width - Pad * 2, RowHeight);
            if (index == _list.Selected)
            {
                c.DrawRect(new DrawRectInputs
                {
                    Position = row,
                    Style = new RectStyle
                    {
                        BackgroundColor = menu.ItemSelectedBackground,
                        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                    },
                    ZIndex = z + 1,
                });
            }

            DrawRow(c, row, _list.Items[index], z + 2);
        }
    }

    private void DrawRow(ICanvas c, RectF row, RankedCompletion ranked, int z)
    {
        var (glyph, tint) = GlyphOf(ranked.Item.Kind);
        _icon.TextColor = tint;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(row.Left, row.Bottom, IconColumn, row.Height),
            Text = glyph,
            Style = _icon,
            ZIndex = z,
        });

        var menu = _styles.ContextMenu;
        _plain.TextColor = menu.ItemText;
        _matched.TextColor = menu.AccentText;

        var label = ranked.Item.Label;
        var matched = new bool[label.Length];
        foreach (var position in ranked.Match.Positions)
            if (position < matched.Length) matched[position] = true;

        var x = row.Left + LabelInset - Pad;
        var right = row.Right - Pad;
        var runStart = 0;
        while (runStart < label.Length && x < right)
        {
            var emphasized = matched[runStart];
            var runEnd = runStart + 1;
            while (runEnd < label.Length && matched[runEnd] == emphasized) runEnd++;

            var run = label[runStart..runEnd];
            var style = emphasized ? _matched : _plain;
            var width = c.MeasureTextWidth(run, style);
            c.DrawText(new DrawTextInputs
            {
                Position = new RectF(x, row.Bottom, MathF.Min(width, right - x), row.Height),
                Text = run,
                Style = style,
                ZIndex = z,
            });
            x += width;
            runStart = runEnd;
        }

        var room = right - x - DetailGap;
        if (ranked.Item.Detail is not { Length: > 0 } detail || room <= 0) return;

        _detail.TextColor = menu.ItemTextDisabled;
        var shown = TextEllipsis.Truncate(c, detail, _detail, MathF.Min(room, MaxDetailWidth));
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(right - MathF.Min(room, MaxDetailWidth), row.Bottom, MathF.Min(room, MaxDetailWidth), row.Height),
            Text = shown,
            Style = _detail,
            ZIndex = z,
        });
    }

    private (string Glyph, uint Tint) GlyphOf(CompletionKind kind)
    {
        var syntax = _styles.DiffContent.Syntax;
        return kind switch
        {
            CompletionKind.Symbol(var symbol) => symbol switch
            {
                SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function =>
                    (LucideIcons.FunctionSquare, syntax.Function),
                SymbolKind.Property or SymbolKind.Field or SymbolKind.Event or SymbolKind.EnumMember =>
                    (LucideIcons.Variable, syntax.Variable),
                _ => (LucideIcons.Box, syntax.Type),
            },
            CompletionKind.Keyword => (LucideIcons.WholeWord, syntax.Keyword),
            CompletionKind.Word => (LucideIcons.WholeWord, _styles.ContextMenu.ItemTextDisabled),
            _ => throw new InvalidOperationException($"Unhandled completion kind {kind}."),
        };
    }
}

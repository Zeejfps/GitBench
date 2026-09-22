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

        // Acquired before the old one is released, so the list never blinks out between keystrokes.
        var previous = _popup;
        _popup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx => Direction.Wrap(new Raw
            {
                View = new CompletionListView(ctx.Canvas, list, ctx.Theme().Styles.Value),
            }).BuildView(ctx),
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
    private const float MaxWidth = 560f;

    /// <summary>How far right of the popup's edge a label starts, so the popup can be placed with
    /// its labels under the word they complete.</summary>
    public const float LabelInset = Pad + IconColumn + 1f;

    private readonly CompletionList _list;
    private readonly ThemeStyles _styles;
    private readonly int _top;
    private readonly int _count;

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

    public CompletionListView(ICanvas canvas, CompletionList list, ThemeStyles styles)
    {
        _list = list;
        _styles = styles;
        _count = Math.Min(list.Items.Count, CompletionSession.VisibleRows);
        _top = Math.Clamp(list.Selected - _count / 2, 0, list.Items.Count - _count);

        var widest = 0f;
        foreach (var ranked in list.Items)
            widest = MathF.Max(widest, canvas.MeasureTextWidth(ranked.Item.Label, _plain));

        Width = Math.Clamp(LabelInset + widest + Pad * 3, MinWidth, MaxWidth);
        Height = _count * RowHeight + Pad * 2;
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

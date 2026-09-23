using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Search;

/// <summary>The results of a search, as a virtualized list over the view model's rows.</summary>
internal sealed record SearchResultsList : Widget
{
    public required SearchEverywhereViewModel Model { get; init; }

    protected override View CreateView(Context ctx) => new SearchResultsView(ctx, Model);
}

/// <summary>
/// Paints <see cref="SearchEverywhereViewModel.Rows"/> and keeps the selected one in view. Holds no
/// copy of the model beyond the published list, so the painter and the keyboard always agree on
/// what row 12 is. A click opens the row, with Shift in a preview tab.
/// </summary>
internal sealed class SearchResultsView : ContainerView
{
    private readonly SearchEverywhereViewModel _model;
    private readonly ICanvas _canvas;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBar _scrollBar;
    private readonly SearchRowPainter _painter = new();

    private IReadOnlyList<SearchRow> _rows = [];

    public SearchResultsView(Context ctx, SearchEverywhereViewModel model)
    {
        _model = model;
        _canvas = ctx.Canvas;
        var input = ctx.Require<InputSystem>();

        _list = new VirtualRowListView
        {
            RowHeightAt = index => index >= 0 && index < _rows.Count ? SearchRowPainter.HeightOf(_rows[index]) : SearchRowPainter.HitHeight,
            ItemBuilder = DrawRowAt,
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.RowClicked += OnRowClicked;
        _scrollBar = ScrollBars.CreateVertical(ctx);

        AddChildToSelf(new BorderLayoutView
        {
            Center = new PaddingView
            {
                Padding = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs, Top = Spacing.Xs, Bottom = Spacing.Xs },
                Children = { _list },
            },
            East = _scrollBar,
        });

        _list.UseController(input, () => new VirtualRowListController(_list));

        this.Bind(model.Rows, SetRows);
        this.Bind(model.Selected, _ => { EnsureSelectedVisible(); SetDirty(); });
        this.Bind(ctx.Localization().Strings, s => { _painter.Strings = s; SetDirty(); });
        this.BindThemed(ctx.Theme(), s => { _painter.Theme = s; SetDirty(); });
        this.Use(() => new ScrollSyncController(_list, _scrollBar));
    }

    private void SetRows(IReadOnlyList<SearchRow> rows)
    {
        _rows = rows;
        _list.ItemCount = rows.Count;
        _list.InvalidateRowHeights();
        _list.NotifyItemsChanged();
        EnsureSelectedVisible();
        SetDirty();
    }

    private void EnsureSelectedVisible()
    {
        var index = _model.Selected.Value;
        if (index >= 0 && index < _rows.Count) _list.EnsureRowVisible(index);
    }

    private void OnRowClicked(int index, InputModifiers modifiers, PointF point)
    {
        if (index < 0 || index >= _rows.Count) return;
        _model.Select(index);
        _model.ActivateAt(index, (modifiers & InputModifiers.Shift) != 0 ? OpenAs.Transient : OpenAs.Pinned);
    }

    private void DrawRowAt(ICanvas c, RectF rowRect, int index, RowRenderState state, int z)
    {
        if (index < 0 || index >= _rows.Count) return;
        _painter.Draw(_canvas, rowRect, _rows[index], index == _model.Selected.Value, state.IsHovered, z, IsRtl);
    }
}

/// <summary>
/// Draws one search row: a group heading, a "more" row, a file, or a symbol — kind icon, name with
/// the matched letters in the accent colour, then muted what it sits in and where it is. A row a
/// language server answered with carries a small dot at its end.
/// </summary>
internal sealed class SearchRowPainter
{
    public const float HitHeight = 28f;
    private const float HeadingHeight = 24f;
    private const float MoreHeight = 24f;
    private const float PaddingLeft = 12f;
    private const float PaddingRight = 12f;
    private const float IconWidth = 16f;
    private const float Gap = 8f;
    private const float ServerDotSize = 6f;

    private readonly TextStyle _icon = new() { HorizontalAlignment = TextAlignment.Center, VerticalAlignment = TextAlignment.Center };
    private readonly TextStyle _name = new() { HorizontalAlignment = TextAlignment.Start, VerticalAlignment = TextAlignment.Center };
    private readonly TextStyle _muted = new() { HorizontalAlignment = TextAlignment.Start, VerticalAlignment = TextAlignment.Center };
    private readonly TextStyle _path = new() { HorizontalAlignment = TextAlignment.End, VerticalAlignment = TextAlignment.Center };
    private readonly TextStyle _heading = new()
    {
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };

    public ThemeStyles Theme { get; set; } = ThemeStyles.Dark;

    public Strings? Strings { get; set; }

    public static float HeightOf(SearchRow row) => row switch
    {
        SearchRow.Heading => HeadingHeight,
        SearchRow.More => MoreHeight,
        SearchRow.FileHit or SearchRow.Symbol => HitHeight,
        _ => throw new InvalidOperationException($"No height for {row.GetType().Name}."),
    };

    public static string TabTitle(Strings s, SearchTab tab) => tab switch
    {
        SearchTab.All => s.SearchEverywhereTabAll,
        SearchTab.Types => s.SearchEverywhereTabTypes,
        SearchTab.Symbols => s.SearchEverywhereTabSymbols,
        SearchTab.Files => s.SearchEverywhereTabFiles,
        _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, "No title."),
    };

    public void Draw(ICanvas canvas, RectF rowRect, SearchRow row, bool isSelected, bool isHovered, int z, bool isRtl)
    {
        switch (row)
        {
            case SearchRow.Heading heading:
                DrawHeading(canvas, rowRect, heading, z, isRtl);
                return;
            case SearchRow.More more:
                RowSelection.DrawBackground(canvas, rowRect, isSelected, isHovered, Theme.RowSelection, z, isRtl);
                DrawMore(canvas, rowRect, more, isSelected, z, isRtl);
                return;
            case SearchRow.FileHit file:
                RowSelection.DrawBackground(canvas, rowRect, isSelected, isHovered, Theme.RowSelection, z, isRtl);
                DrawFile(canvas, rowRect, file, isSelected, z, isRtl);
                return;
            case SearchRow.Symbol symbol:
                RowSelection.DrawBackground(canvas, rowRect, isSelected, isHovered, Theme.RowSelection, z, isRtl);
                DrawSymbol(canvas, rowRect, symbol.Hit, isSelected, z, isRtl);
                return;
            default:
                throw new InvalidOperationException($"No painter for {row.GetType().Name}.");
        }
    }

    private void DrawHeading(ICanvas canvas, RectF rowRect, SearchRow.Heading heading, int z, bool isRtl)
    {
        if (Strings is not { } s) return;
        _heading.TextColor = Theme.Palette.TextMuted;
        var width = rowRect.Width - PaddingLeft - PaddingRight;
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, rowRect.Left + PaddingLeft, width, isRtl),
            Text = TabTitle(s, heading.Group),
            Style = _heading,
            ZIndex = z + 2,
        });
    }

    private void DrawMore(ICanvas canvas, RectF rowRect, SearchRow.More more, bool isSelected, int z, bool isRtl)
    {
        if (Strings is not { } s) return;
        _muted.FontSize = FontSize.Caption;
        _muted.TextColor = isSelected ? Theme.RowSelection.Text : Theme.Palette.Accent;
        var left = rowRect.Left + PaddingLeft + IconWidth + Gap;
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, rowRect.Right - PaddingRight - left, isRtl),
            Text = s.SearchEverywhereMore(TabTitle(s, more.Tab)),
            Style = _muted,
            ZIndex = z + 2,
        });
    }

    private void DrawFile(ICanvas canvas, RectF rowRect, SearchRow.FileHit file, bool isSelected, int z, bool isRtl)
    {
        var slash = file.Path.LastIndexOf('/');
        var name = slash < 0 ? file.Path : file.Path[(slash + 1)..];
        var directory = slash < 0 ? string.Empty : file.Path[..slash];

        var (glyph, family) = FileGlyph.For(name);
        var left = DrawIcon(canvas, rowRect, glyph, family,
            Theme.FileBrowserRow.IconFor(FileKinds.Classify(name)), z, isRtl);

        var right = rowRect.Right - PaddingRight;
        left = DrawName(canvas, rowRect, name, file.NameHighlights, left, right, isSelected, z, isRtl);

        var place = file.At is { } at ? $":{at.Line.Value}" : string.Empty;
        DrawMuted(canvas, rowRect, directory + place, left + Gap, right, isSelected, z, isRtl);
    }

    private void DrawSymbol(ICanvas canvas, RectF rowRect, SymbolHit hit, bool isSelected, int z, bool isRtl)
    {
        var row = hit.Row;
        var left = DrawIcon(canvas, rowRect, FileBrowserRowPainter.SymbolGlyph(row.Kind), LucideIcons.FontFamily,
            Theme.FileBrowserRow.FileIcon, z, isRtl);

        var right = rowRect.Right - PaddingRight;
        if (hit.Source == SymbolSource.Server)
        {
            DrawServerDot(canvas, rowRect, right, z, isRtl);
            right -= ServerDotSize + Gap;
        }

        // The path takes at most two fifths of the row, from the end, so the name always has room.
        _path.FontSize = FontSize.Caption;
        var pathWidth = MathF.Min(canvas.MeasureTextWidth(row.Path, _path), (right - left) * 0.4f);
        if (pathWidth > 0f)
        {
            _path.TextColor = MutedColor(isSelected);
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, right - pathWidth, pathWidth, isRtl),
                Text = PathTail(canvas, row.Path, _path, pathWidth),
                Style = _path,
                ZIndex = z + 3,
            });
            right -= pathWidth + Gap;
        }

        left = DrawName(canvas, rowRect, row.Name, hit.Highlights, left, right, isSelected, z, isRtl);

        var detail = (row.IsType ? null : row.Container) is { } container
            ? row.ParameterTypes is { } parameters ? $"{container} ({parameters})" : container
            : row.ParameterTypes is { } only ? $"({only})" : string.Empty;
        DrawMuted(canvas, rowRect, detail, left + Gap, right, isSelected, z, isRtl);
    }

    private float DrawIcon(ICanvas canvas, RectF rowRect, string glyph, string family, uint color, int z, bool isRtl)
    {
        var left = rowRect.Left + PaddingLeft;
        _icon.FontFamily = family;
        _icon.FontSize = FileGlyph.SizeOf(family);
        _icon.TextColor = color;
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, IconWidth, isRtl),
            Text = glyph,
            Style = _icon,
            ZIndex = z + 2,
        });
        return left + IconWidth + Gap;
    }

    /// <summary>The name in runs, the matched letters in the accent colour; answers where it ended.</summary>
    private float DrawName(
        ICanvas canvas, RectF rowRect, string name, IReadOnlyList<int> highlights,
        float left, float right, bool isSelected, int z, bool isRtl)
    {
        var available = right - left;
        if (available <= 0f) return left;

        _name.FontSize = FontSize.Body;
        var plain = isSelected ? Theme.RowSelection.Text : Theme.Palette.TextPrimary;
        var fitted = TextEllipsis.Truncate(canvas, name, _name, available);
        if (fitted.Length != name.Length || highlights.Count == 0)
        {
            _name.TextColor = plain;
            var width = canvas.MeasureTextWidth(fitted, _name);
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, left, width, isRtl),
                Text = fitted,
                Style = _name,
                ZIndex = z + 3,
            });
            return left + width;
        }

        var x = left;
        var start = 0;
        while (start < name.Length)
        {
            var lit = Contains(highlights, start);
            var end = start + 1;
            while (end < name.Length && Contains(highlights, end) == lit) end++;

            var run = name[start..end];
            _name.TextColor = lit ? Theme.Palette.Accent : plain;
            var width = canvas.MeasureTextWidth(run, _name);
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, x, width, isRtl),
                Text = run,
                Style = _name,
                ZIndex = z + 3,
            });
            x += width;
            start = end;
        }

        return x;
    }

    private void DrawMuted(ICanvas canvas, RectF rowRect, string text, float left, float right, bool isSelected, int z, bool isRtl)
    {
        var available = right - left;
        if (text.Length == 0 || available <= 0f) return;

        _muted.FontSize = FontSize.Caption;
        _muted.TextColor = MutedColor(isSelected);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, available, isRtl),
            Text = TextEllipsis.Truncate(canvas, text, _muted, available),
            Style = _muted,
            ZIndex = z + 3,
        });
    }

    private void DrawServerDot(ICanvas canvas, RectF rowRect, float right, int z, bool isRtl)
    {
        var left = right - ServerDotSize;
        var bottom = rowRect.Bottom + (rowRect.Height - ServerDotSize) * 0.5f;
        canvas.DrawRect(new DrawRectInputs
        {
            Position = Place(rowRect, left, ServerDotSize, isRtl, bottom, ServerDotSize),
            Style = new RectStyle
            {
                BackgroundColor = Theme.Palette.Accent,
                BorderRadius = BorderRadiusStyle.All(ServerDotSize * 0.5f),
            },
            ZIndex = z + 3,
        });
    }

    private uint MutedColor(bool isSelected) => isSelected ? Theme.RowSelection.Text : Theme.Palette.TextMuted;

    private static bool Contains(IReadOnlyList<int> sorted, int index)
    {
        foreach (var at in sorted)
            if (at == index)
                return true;
        return false;
    }

    /// <summary>As much of a path as fits, counted from its end, cut on separators.</summary>
    private static string PathTail(ICanvas canvas, string path, TextStyle style, float available)
    {
        if (canvas.MeasureTextWidth(path, style) <= available) return path;

        var start = 0;
        while (true)
        {
            var slash = path.IndexOf('/', start);
            if (slash < 0) return TextEllipsis.Truncate(canvas, path[start..], style, available);

            start = slash + 1;
            var tail = "…/" + path[start..];
            if (canvas.MeasureTextWidth(tail, style) <= available) return tail;
        }
    }

    private static RectF Place(in RectF rowRect, float left, float width, bool isRtl) =>
        Place(rowRect, left, width, isRtl, rowRect.Bottom, rowRect.Height);

    private static RectF Place(in RectF rowRect, float left, float width, bool isRtl, float bottom, float height) =>
        isRtl
            ? new RectF(rowRect.Left + rowRect.Right - left - width, bottom, width, height)
            : new RectF(left, bottom, width, height);
}

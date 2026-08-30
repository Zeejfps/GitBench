using GitBench.Controls;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;

namespace GitBench.Features.Terminal;

/// <summary>
/// Where a point on the pane falls in the shell's grid.
/// </summary>
/// <remarks>
/// The mapping belongs to whatever drew the screen: the cell size is measured against the canvas on
/// every draw, and how far back the reader has scrolled is part of the same picture. A point over
/// the history has no cell on the live screen and is not reported at all.
/// </remarks>
internal interface ITerminalCellGeometry
{
    /// <summary>
    /// The cell under <paramref name="point"/> in the live screen's coordinates, or false when the
    /// pane has not been drawn, the point is outside it, or it is over the history.
    /// </summary>
    bool TryLocate(PointF point, out int column, out int row);

    /// <summary>
    /// The nearest cell to <paramref name="point"/> in the grid's own coordinates, where the history
    /// sits at negative rows. Null only when the pane has not been drawn a screen yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryLocate"/>, and named for a different coordinate system, because a
    /// mouse report must never name a row that scrolled off the screen while a drag must keep
    /// tracking wherever the pointer goes. One method answering both would put a history row within
    /// reach of the mouse encoder.
    /// </remarks>
    GridPoint? ClampToGrid(PointF point);

    /// <summary>Asks for the screen to be drawn again, because the selection moved under a drag.</summary>
    void RequestRedraw();
}

/// <summary>
/// Draws a terminal's screen: one background rectangle and one glyph run per style run, a row at a
/// time, plus the cursor.
/// </summary>
/// <remarks>
/// <para>
/// The cell size is measured from the canvas on every draw rather than assumed from the font size,
/// because it is what decides how many columns and rows the shell is told it has — and a grid whose
/// arithmetic disagrees with the one the canvas draws on has box-drawing characters that do not
/// meet. It is also the only place the viewport can be measured: a cell has no size until there is
/// a canvas to measure it against.
/// </para>
/// <para>
/// The screen is read straight out of the live grid during the draw. That is safe for exactly one
/// reason — the engine is only ever fed on the UI thread — and it is what keeps a full-screen
/// repaint free of a screen-sized copy.
/// </para>
/// </remarks>
internal sealed class TerminalGridView : View, ITerminalCellGeometry
{
    const float CaretThickness = 2f;

    static readonly TextStyle MetricsStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        BaseDirection = BidiDirection.Ltr,
    };

    readonly TextStyle _runStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        BaseDirection = BidiDirection.Ltr,
    };

    readonly TextStyle _messageStyle = new()
    {
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    // Reused across every rectangle a draw stages: DrawRect copies the style's fields out and keeps
    // no reference, and a fresh one per background run per row per frame is the pane's largest
    // source of garbage.
    readonly RectStyle _rectStyle = new();

    TerminalStyles _styles = ThemeStyles.Dark.Terminal;
    ICellStyler _styler = new TerminalCellStyler(ThemeStyles.Dark.Terminal);
    uint _messageColor = ThemeStyles.Dark.Palette.TextSecondary;

    TerminalRenderState _render = new TerminalRenderState.Starting();
    string _startingMessage = string.Empty;

    CellGeometry? _geometry;

    TerminalCell[] _cells = [];
    int[] _codePoints = [];
    TerminalRowRun[] _runs = [];
    TerminalSize? _reported;

    public TerminalGridView(IThemeService<ThemeStyles> theme)
    {
        this.BindThemed(theme, s =>
        {
            _styles = s.Terminal;
            _styler = new TerminalCellStyler(s.Terminal);
            _messageColor = s.Palette.TextSecondary;
            SetDirty();
        });
    }

    /// <summary>Raised from a draw once the pane knows how many cells it can show.</summary>
    public Action<TerminalSize>? OnViewportChanged { get; set; }

    public void SetRenderState(TerminalRenderState render)
    {
        _render = render;
        SetDirty();
    }

    /// <summary>The line shown while the shell is starting.</summary>
    public string StartingMessage
    {
        get => _startingMessage;
        set
        {
            _startingMessage = value;
            SetDirty();
        }
    }

    /// <summary>Asks for the screen to be drawn again, because the shell has printed.</summary>
    /// <remarks>
    /// <para>
    /// A pane kept alive behind another mode is still fed by its shell, so a build running in a
    /// terminal nobody is looking at would otherwise ask the window for a frame per drain — and get
    /// one, spent drawing whatever mode is actually on screen. Becoming visible again asks for a
    /// frame on its own, and the screen is read live from the engine when it is drawn, so a repaint
    /// declined here is never a repaint missed.
    /// </para>
    /// <para>
    /// A repaint rather than a full invalidation: this view's size comes from the constraint it is
    /// given and never from the screen it draws, so invalidating the measure of every ancestor up to
    /// the root each time the shell prints a line cannot change any of them.
    /// </para>
    /// </remarks>
    public void Repaint()
    {
        for (View? view = this; view is not null; view = view.Parent)
            if (!view.IsVisible)
                return;

        MarkVisualDirty();
    }

    public void RequestRedraw() => Repaint();

    public bool TryLocate(PointF point, out int column, out int row)
    {
        column = 0;
        row = 0;

        if (_geometry is not { } cells) return false;
        if (!cells.Bounds.ContainsPoint(point)) return false;

        column = Math.Clamp(
            (int)((point.X - cells.Bounds.Left) / cells.Advance),
            0,
            cells.Columns - 1);

        var live = (int)((cells.Bounds.Top - point.Y) / cells.Height) - cells.Offset;
        if (live < 0) return false;

        row = Math.Min(live, cells.Rows - 1);
        return true;
    }

    public GridPoint? ClampToGrid(PointF point)
    {
        if (_geometry is not { } cells) return null;
        if (cells.Advance <= 0f || cells.Height <= 0f || cells.Columns <= 0) return null;

        var column = Math.Clamp(
            (int)MathF.Floor((point.X - cells.Bounds.Left) / cells.Advance),
            0,
            cells.Columns - 1);

        var row = (int)MathF.Floor((cells.Bounds.Top - point.Y) / cells.Height) - cells.Offset;

        return new GridPoint(column, Math.Clamp(row, -cells.Offset, cells.Rows - 1));
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var bounds = Position;
        var z = GetDrawZIndex();

        _rectStyle.BackgroundColor = _styles.DefaultBackground;

        c.DrawRect(new DrawRectInputs
        {
            Position = bounds,
            Style = _rectStyle,
            ZIndex = z,
        });

        if (bounds.Width <= 0f || bounds.Height <= 0f) return;

        var metrics = c.MeasureCellSize(MetricsStyle);
        if (metrics.Advance <= 0f || metrics.Height <= 0f) return;

        ReportViewport(bounds, metrics);

        // Every state, exhaustively, and a default that throws rather than quietly drawing something
        // for a state nobody thought about: what is on screen for each of them is the whole of what
        // this view does, and a missed one is a pane that silently shows the wrong thing.
        switch (_render)
        {
            case TerminalRenderState.Idle:
                _geometry = null;
                break;
            case TerminalRenderState.Starting:
                _geometry = null;
                DrawMessage(c, bounds, _startingMessage, z + 1);
                break;
            case TerminalRenderState.Running running:
                DrawSession(c, running.Session, bounds, metrics, z);
                break;
            case TerminalRenderState.Exited exited:
                DrawSession(c, exited.Session, bounds, metrics, z);
                break;
            case TerminalRenderState.Faulted faulted:
                DrawSession(c, faulted.Session, bounds, metrics, z);
                break;
            case TerminalRenderState.Failed failed:
                _geometry = null;
                DrawMessage(c, bounds, failed.Message, z + 1);
                break;
            default:
                throw new NotSupportedException($"No terminal drawing for {_render.GetType().Name}.");
        }
    }

    /// <remarks>
    /// A shell that has finished still has a screen, and it keeps its geometry with it: the history
    /// it printed is what a reader most wants to scroll and select once it has stopped moving.
    /// </remarks>
    void DrawSession(ICanvas c, TerminalSession session, RectF bounds, CellMetrics metrics, int z)
    {
        c.PushClip(bounds);
        DrawScreen(c, session, bounds, metrics, z + 1);
        c.PopClip();
    }

    void ReportViewport(RectF bounds, CellMetrics metrics)
    {
        var columns = Math.Max(1, (int)(bounds.Width / metrics.Advance));
        var rows = Math.Max(1, (int)(bounds.Height / metrics.Height));
        var size = new TerminalSize(columns, rows);

        if (_reported == size) return;
        _reported = size;
        OnViewportChanged?.Invoke(size);
    }

    void DrawScreen(ICanvas c, TerminalSession session, RectF bounds, CellMetrics metrics, int z)
    {
        var grid = session.Grid;
        var size = grid.Size;
        EnsureBuffers(size.Columns);

        // The grid is only resized on the next frame after the pane changes shape, so a draw can
        // land on a screen taller than the space it has. Drawing what fits beats drawing past the
        // bottom edge for the one frame it takes to catch up.
        var visibleRows = Math.Min(size.Rows, (int)(bounds.Height / metrics.Height) + 1);

        // How far back the reader has scrolled, in the grid's own coordinates: history sits at
        // negative rows, so the top of the pane is row -offset and the screen slides down from
        // there. The session clamps this to the history that exists, which is what keeps a row
        // reference on the grid rather than off the top of it.
        var offset = session.ScrollOffset;

        _geometry = new CellGeometry(
            bounds,
            metrics.Advance,
            metrics.Height,
            offset,
            size.Columns,
            size.Rows);

        var selection = session.Selection;

        for (var row = 0; row < visibleRows; row++)
        {
            var top = bounds.Top - row * metrics.Height;
            grid.CopyRow(row - offset, _cells.AsSpan(0, size.Columns));
            DrawRow(c, _cells.AsSpan(0, size.Columns), bounds.Left, top, metrics, z);
            DrawSelection(c, selection, row - offset, size.Columns, bounds.Left, top, metrics, z + 1);
        }

        DrawCursor(c, grid, session.State.Cursor, offset, visibleRows, bounds, metrics, z);
    }

    void DrawRow(ICanvas c, ReadOnlySpan<TerminalCell> cells, float left, float top, CellMetrics metrics, int z)
    {
        var row = TerminalRowRuns.Split(cells, _styler, _codePoints, _runs);

        foreach (var run in row.Runs)
        {
            var x = left + run.Column * metrics.Advance;
            var width = run.Length * metrics.Advance;

            // The pane already painted the default background across its whole area, so a run that
            // matches it is a rectangle nobody would see.
            if (run.Style.Background != _styles.DefaultBackground)
            {
                _rectStyle.BackgroundColor = run.Style.Background;

                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(x, top - metrics.Height, width, metrics.Height),
                    Style = _rectStyle,
                    ZIndex = z,
                });
            }

            var codePoints = row.CodePointsOf(run);

            // A row is padded to the full width of the screen, and the padding carries the style of
            // whatever preceded it, so most of what a run covers on a shell prompt is blank. Each of
            // those columns costs a font lookup and draws nothing. The background rectangle above is
            // already the run's full width, so dropping them changes nothing on screen — except
            // under a decoration, which is drawn to the length of the run and would come up short.
            if (!run.Style.Underline && !run.Style.StrikeThrough)
                codePoints = TrimTrailingBlanks(codePoints);

            if (codePoints.IsEmpty) continue;

            _runStyle.FontFamily = Face(run.Style);
            _runStyle.TextColor = run.Style.Foreground;

            c.DrawGlyphRun(new DrawGlyphRunInputs
            {
                Origin = new PointF(x, top),
                CodePoints = codePoints,
                CellAdvance = metrics.Advance,
                Style = _runStyle,
                ZIndex = z + 2,
                Underline = run.Style.Underline,
                StrikeThrough = run.Style.StrikeThrough,
            });
        }
    }

    /// <remarks>
    /// Under the glyphs and over the row's own backgrounds, which is the layer the block cursor
    /// already uses: the text keeps its colours and stays legible over the highlight, so a selected
    /// run needs no restyling and the row splitting is untouched.
    /// </remarks>
    void DrawSelection(
        ICanvas c,
        TerminalSpan? selection,
        int gridRow,
        int columns,
        float left,
        float top,
        CellMetrics metrics,
        int z)
    {
        if (selection is not { } span) return;
        if (!span.TryColumnsOn(gridRow, columns, out var first, out var last)) return;

        _rectStyle.BackgroundColor = _styles.Selection;

        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(
                left + first * metrics.Advance,
                top - metrics.Height,
                (last - first + 1) * metrics.Advance,
                metrics.Height),
            Style = _rectStyle,
            ZIndex = z,
        });
    }

    /// <remarks>
    /// The cursor is on the live screen, so scrolling back pushes it down the pane and eventually
    /// off the bottom of it — where it is not drawn at all, rather than clamped to the last row and
    /// left claiming a position the shell is not at.
    /// </remarks>
    void DrawCursor(
        ICanvas c,
        ITerminalGrid grid,
        TerminalCursor cursor,
        int offset,
        int visibleRows,
        RectF bounds,
        CellMetrics metrics,
        int z)
    {
        if (!cursor.Visible) return;

        var screenRow = cursor.Row + offset;
        if (screenRow >= visibleRows) return;

        var left = bounds.Left + cursor.Column * metrics.Advance;
        var top = bounds.Top - screenRow * metrics.Height;

        var rect = cursor.Shape switch
        {
            CursorShape.Underline => new RectF(left, top - metrics.Height, metrics.Advance, CaretThickness),
            CursorShape.Bar => new RectF(left, top - metrics.Height, CaretThickness, metrics.Height),
            _ => new RectF(left, top - metrics.Height, metrics.Advance, metrics.Height),
        };

        _rectStyle.BackgroundColor = _styles.Cursor;

        c.DrawRect(new DrawRectInputs
        {
            Position = rect,
            Style = _rectStyle,
            ZIndex = z + 1,
        });

        if (cursor.Shape != CursorShape.Block) return;

        // A block fills the whole cell, so the glyph the row already drew is behind it. Terminals
        // read it back out by inverting that one cell: the character is drawn again over the block
        // in the colour it was sitting on.
        DrawCursorGlyph(c, grid, cursor, left, top, metrics, z + 3);
    }

    void DrawCursorGlyph(
        ICanvas c,
        ITerminalGrid grid,
        TerminalCursor cursor,
        float left,
        float top,
        CellMetrics metrics,
        int z)
    {
        var columns = grid.Size.Columns;
        if (cursor.Column < 0 || cursor.Column >= columns) return;

        grid.CopyRow(cursor.Row, _cells.AsSpan(0, columns));

        ref readonly var cell = ref _cells[cursor.Column];
        if (cell.Width == CellWidth.WideTrailer) return;

        var codePoint = cell.Rune.Value;
        if (codePoint == ' ' || codePoint == 0) return;

        var style = _styler.Style(cell);

        _runStyle.FontFamily = Face(style);
        _runStyle.TextColor = style.Background;

        _codePoints[0] = codePoint;

        c.DrawGlyphRun(new DrawGlyphRunInputs
        {
            Origin = new PointF(left, top),
            CodePoints = _codePoints.AsSpan(0, 1),
            CellAdvance = metrics.Advance,
            Style = _runStyle,
            ZIndex = z,
            Underline = style.Underline,
            StrikeThrough = style.StrikeThrough,
        });
    }

    void DrawMessage(ICanvas c, RectF bounds, string message, int z)
    {
        if (string.IsNullOrEmpty(message)) return;

        _messageStyle.TextColor = _messageColor;

        c.DrawText(new DrawTextInputs
        {
            Position = bounds,
            Text = message,
            Style = _messageStyle,
            ZIndex = z,
        });
    }

    void EnsureBuffers(int columns)
    {
        if (_cells.Length >= columns) return;

        _cells = new TerminalCell[columns];
        _codePoints = new int[columns];
        _runs = new TerminalRowRun[columns];
    }

    /// <remarks>
    /// Bold picks the bold face rather than a brighter colour. The styler has already spent every
    /// attribute that means a colour, so what arrives here can only mean a shape.
    /// </remarks>
    static string Face(in RunStyle style) => (style.Bold, style.Italic) switch
    {
        (true, true) => MonoFonts.BoldItalic,
        (true, false) => MonoFonts.Bold,
        (false, true) => MonoFonts.Italic,
        _ => MonoFonts.Regular,
    };

    readonly record struct CellGeometry(
        RectF Bounds,
        float Advance,
        float Height,
        int Offset,
        int Columns,
        int Rows);
    static ReadOnlySpan<int> TrimTrailingBlanks(ReadOnlySpan<int> codePoints)
    {
        var end = codePoints.Length;
        while (end > 0 && codePoints[end - 1] == ' ') end--;
        return codePoints[..end];
    }

}

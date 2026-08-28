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
internal sealed class TerminalGridView : View
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

    TerminalStyles _styles = ThemeStyles.Dark.Terminal;
    ICellStyler _styler = new TerminalCellStyler(ThemeStyles.Dark.Terminal);
    uint _messageColor = ThemeStyles.Dark.Palette.TextSecondary;

    TerminalRenderState _render = new TerminalRenderState.Starting();
    string _startingMessage = string.Empty;

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

    public void Repaint() => SetDirty();

    protected override void OnDrawSelf(ICanvas c)
    {
        var bounds = Position;
        var z = GetDrawZIndex();

        c.DrawRect(new DrawRectInputs
        {
            Position = bounds,
            Style = new RectStyle { BackgroundColor = _styles.DefaultBackground },
            ZIndex = z,
        });

        if (bounds.Width <= 0f || bounds.Height <= 0f) return;

        var metrics = c.MeasureCellSize(MetricsStyle);
        if (metrics.Advance <= 0f || metrics.Height <= 0f) return;

        ReportViewport(bounds, metrics);

        switch (_render)
        {
            case TerminalRenderState.Running running:
                c.PushClip(bounds);
                DrawScreen(c, running.Session, bounds, metrics, z + 1);
                c.PopClip();
                break;
            case TerminalRenderState.Failed failed:
                DrawMessage(c, bounds, failed.Message, z + 1);
                break;
            default:
                DrawMessage(c, bounds, _startingMessage, z + 1);
                break;
        }
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

        for (var row = 0; row < visibleRows; row++)
        {
            var top = bounds.Top - row * metrics.Height;
            grid.CopyRow(row, _cells.AsSpan(0, size.Columns));
            DrawRow(c, _cells.AsSpan(0, size.Columns), bounds.Left, top, metrics, z);
        }

        DrawCursor(c, session.State.Cursor, bounds, metrics, z + 2);
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
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(x, top - metrics.Height, width, metrics.Height),
                    Style = new RectStyle { BackgroundColor = run.Style.Background },
                    ZIndex = z,
                });
            }

            _runStyle.FontFamily = Face(run.Style);
            _runStyle.TextColor = run.Style.Foreground;

            c.DrawGlyphRun(new DrawGlyphRunInputs
            {
                Origin = new PointF(x, top),
                CodePoints = row.CodePointsOf(run),
                CellAdvance = metrics.Advance,
                Style = _runStyle,
                ZIndex = z + 1,
                Underline = run.Style.Underline,
                StrikeThrough = run.Style.StrikeThrough,
            });
        }
    }

    void DrawCursor(ICanvas c, TerminalCursor cursor, RectF bounds, CellMetrics metrics, int z)
    {
        if (!cursor.Visible) return;

        var left = bounds.Left + cursor.Column * metrics.Advance;
        var top = bounds.Top - cursor.Row * metrics.Height;

        var rect = cursor.Shape switch
        {
            CursorShape.Underline => new RectF(left, top - metrics.Height, metrics.Advance, CaretThickness),
            CursorShape.Bar => new RectF(left, top - metrics.Height, CaretThickness, metrics.Height),
            _ => new RectF(left, top - metrics.Height, metrics.Advance, metrics.Height),
        };

        c.DrawRect(new DrawRectInputs
        {
            Position = rect,
            Style = new RectStyle { BackgroundColor = _styles.Cursor },
            ZIndex = z,
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
}

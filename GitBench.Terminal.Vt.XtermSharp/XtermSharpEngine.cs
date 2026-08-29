using XtermSharp;
using XtermTerminal = XtermSharp.Terminal;

namespace GitBench.Terminal.Vt.Adapters;

/// <summary>
/// The vendored XtermSharp core behind <see cref="ITerminalEngine"/>.
/// </summary>
/// <remarks>
/// The adapter translates; it never compensates. Where XtermSharp cannot answer what the seam asks
/// for, the state is reported at the engine's honest default and the test that wanted it fails.
/// Every such hole is listed in KnownGaps.md with the vendored file and line that causes it, so a
/// gap stays a patch to that source rather than becoming a special case here.
/// </remarks>
public sealed class XtermSharpEngine : ITerminalEngine
{
    readonly XtermTerminal terminal;
    readonly ResponseSink responses;
    readonly XtermGrid grid;

    byte[] feedBuffer = new byte[4096];

    public XtermSharpEngine(TerminalSetup setup)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(setup.ScrollbackLines);

        responses = new ResponseSink();
        terminal = new XtermTerminal(responses, new TerminalOptions
        {
            Cols = setup.Size.Columns,
            Rows = setup.Size.Rows,
            TermName = "xterm-256color",
            ConvertEol = false,
            Scrollback = setup.ScrollbackLines,
        });
        grid = new XtermGrid(terminal);
        terminal.ClearUpdateRange();
    }

    public ITerminalGrid Grid => grid;

    public TerminalState State => new(
        ReadCursor(),
        ReadModes(),
        terminal.Title ?? string.Empty,
        terminal.IconTitle ?? string.Empty);

    public FeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return FeedResult.Nothing;

        if (feedBuffer.Length < bytes.Length)
            feedBuffer = new byte[bytes.Length];
        bytes.CopyTo(feedBuffer);

        var historyBefore = terminal.ScrolledIntoHistory;
        var framesBefore = terminal.SynchronizedFrames;
        terminal.ClearUpdateRange();
        terminal.Feed(feedBuffer, bytes.Length);
        terminal.GetUpdateRange(out var first, out var last);

        return new FeedResult(
            Damage: last < first
                ? RowSpan.None
                : new RowSpan(Math.Max(first, 0), Math.Min(last, terminal.Rows - 1)),
            Response: responses.Drain(),
            FramesCompleted: terminal.SynchronizedFrames - framesBefore,
            FramePending: terminal.SynchronizedUpdate,
            LinesScrolled: terminal.ScrolledIntoHistory - historyBefore);
    }

    public void Resize(TerminalSize size) => terminal.Resize(size.Columns, size.Rows);

    public void Dispose()
    {
    }

    TerminalCursor ReadCursor()
    {
        var (shape, blinking) = ToShapeAndBlink(terminal.CursorStyle);
        return new TerminalCursor(
            terminal.Buffer.X,
            terminal.Buffer.Y,
            Visible: !terminal.CursorHidden,
            Shape: shape,
            Blinking: blinking);
    }

    static (CursorShape Shape, bool Blinking) ToShapeAndBlink(CursorStyle style) => style switch
    {
        CursorStyle.BlinkBlock => (CursorShape.Block, true),
        CursorStyle.SteadyBlock => (CursorShape.Block, false),
        CursorStyle.BlinkUnderline => (CursorShape.Underline, true),
        CursorStyle.SteadyUnderline => (CursorShape.Underline, false),
        CursorStyle.BlinkingBar => (CursorShape.Bar, true),
        CursorStyle.SteadyBar => (CursorShape.Bar, false),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown cursor style."),
    };

    TerminalModes ReadModes() => new(
        ApplicationCursorKeys: terminal.ApplicationCursor,
        ApplicationKeypad: terminal.ApplicationKeypad,
        AutoWrap: terminal.Wraparound,
        AlternateScreen: terminal.Buffers.IsAlternateBuffer,
        BracketedPaste: terminal.BracketedPasteMode,
        FocusReporting: terminal.SendFocus,
        SynchronizedOutput: terminal.SynchronizedUpdate,
        MouseTracking: terminal.MouseMode switch
        {
            MouseMode.Off => MouseTracking.Off,
            MouseMode.X10 => MouseTracking.X10,
            MouseMode.VT200 => MouseTracking.Normal,
            MouseMode.ButtonEventTracking => MouseTracking.ButtonEvent,
            MouseMode.AnyEvent => MouseTracking.AnyEvent,
            _ => MouseTracking.Off,
        },
        MouseEncoding: terminal.MouseProtocol switch
        {
            MouseProtocolEncoding.X10 => MouseEncoding.X10,
            MouseProtocolEncoding.UTF8 => MouseEncoding.Utf8,
            MouseProtocolEncoding.SGR => MouseEncoding.Sgr,
            MouseProtocolEncoding.URXVT => MouseEncoding.Urxvt,
            _ => MouseEncoding.X10,
        },
        KeyboardProtocolFlags: terminal.KeyboardProtocolFlags,
        ModifyOtherKeys: terminal.ModifyOtherKeys);

    /// <summary>Reads XtermSharp's active buffer in the grid surface's coordinate system.</summary>
    sealed class XtermGrid(XtermTerminal terminal) : ITerminalGrid
    {
        public TerminalSize Size => new(terminal.Cols, terminal.Rows);

        public int ScrollbackRows => terminal.Buffer.YBase;

        public void CopyRow(int row, Span<TerminalCell> destination)
        {
            var columns = terminal.Cols;
            ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, columns);
            ArgumentOutOfRangeException.ThrowIfLessThan(row, -ScrollbackRows);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, terminal.Rows);

            var line = terminal.Buffer.Lines[terminal.Buffer.YBase + row];
            for (var column = 0; column < columns; column++)
                destination[column] = column < line.Length ? Translate(line[column]) : TerminalCell.Blank;
        }

        public bool ContinuesPreviousRow(int row)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(row, -ScrollbackRows);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, terminal.Rows);

            return terminal.Buffer.Lines[terminal.Buffer.YBase + row].IsWrapped;
        }

        static TerminalCell Translate(CharData cell) => new(
            cell.IsNullChar() ? new Rune(' ') : ToRune(cell.Code),
            ToColor(cell.Attribute.Foreground),
            ToColor(cell.Attribute.Background),
            ToAttributes(cell.Attribute.Flags),
            cell.Width switch
            {
                0 => CellWidth.WideTrailer,
                2 => CellWidth.WideLeader,
                _ => CellWidth.Single,
            })
        {
            Combining = cell.Combining,
        };

        static Rune ToRune(int code) => Rune.IsValid(code) ? new Rune(code) : Rune.ReplacementChar;

        static TerminalColor ToColor(CellColor colour) => colour.Kind switch
        {
            CellColorKind.Default => TerminalColor.Default,
            CellColorKind.InvertedDefault => TerminalColor.Default,
            CellColorKind.Indexed => TerminalColor.Indexed(colour.Index),
            CellColorKind.Rgb => TerminalColor.Rgb(colour.Red, colour.Green, colour.Blue),
            _ => throw new ArgumentOutOfRangeException(nameof(colour), colour.Kind, "Unknown cell colour kind."),
        };

        static CellAttributes ToAttributes(FLAGS flags)
        {
            var attributes = CellAttributes.None;
            if (flags.HasFlag(FLAGS.BOLD)) attributes |= CellAttributes.Bold;
            if (flags.HasFlag(FLAGS.DIM)) attributes |= CellAttributes.Dim;
            if (flags.HasFlag(FLAGS.ITALIC)) attributes |= CellAttributes.Italic;
            if (flags.HasFlag(FLAGS.UNDERLINE)) attributes |= CellAttributes.Underline;
            if (flags.HasFlag(FLAGS.BLINK)) attributes |= CellAttributes.Blink;
            if (flags.HasFlag(FLAGS.INVERSE)) attributes |= CellAttributes.Inverse;
            if (flags.HasFlag(FLAGS.INVISIBLE)) attributes |= CellAttributes.Hidden;
            if (flags.HasFlag(FLAGS.CrossedOut)) attributes |= CellAttributes.CrossedOut;
            return attributes;
        }
    }

    /// <summary>Collects the bytes the engine owes the program.</summary>
    sealed class ResponseSink : SimpleTerminalDelegate
    {
        readonly List<byte> pending = [];

        public override void Send(byte[] data) => pending.AddRange(data);

        public ReadOnlyMemory<byte> Drain()
        {
            if (pending.Count == 0)
                return ReadOnlyMemory<byte>.Empty;

            var response = pending.ToArray();
            pending.Clear();
            return response;
        }
    }
}

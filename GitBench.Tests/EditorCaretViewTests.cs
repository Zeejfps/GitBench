using System.Runtime.InteropServices;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public class EditorCaretViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;
    private const float ViewHeight = 600f;

    private static readonly bool IsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    private static readonly InputModifiers Command =
        IsMac ? InputModifiers.Super : InputModifiers.Control;
    private static readonly InputModifiers Word =
        IsMac ? InputModifiers.Alt : InputModifiers.Control;

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private sealed class KeyProbe : KeyboardMouseController
    {
        public readonly List<KeyboardKey> Seen = new();

        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            if (e.State == InputState.Pressed) Seen.Add(e.Key);
        }
    }

    private static (GuiTestHarness Harness, DiffContentView View, KeyProbe Probe) Create()
    {
        DiffContentView view = null!;
        var probe = new KeyProbe();
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: (int)ViewHeight,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
            });
        harness.Input.RegisterController(view, probe, EventPhaseFilter.Bubble);
        return (harness, view, probe);
    }

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static EditorBuffer Buffer(params string[] lines) => Buffer(false, lines);

    private static EditorBuffer Buffer(bool endsWithNewline, params string[] lines) =>
        EditorBuffer.TryOpen(
            "file.cs",
            FilePreviewFixture.Of(lines, endsWithNewline),
            new FileWriteBack.Reversible(
                new FileEncoding(FileCharset.Utf8, LineEnding.Lf, endsWithNewline)),
            highlight: null,
            Loc())!;

    private static DiffRenderState.FullFile State(params string[] lines) =>
        new("file.cs", lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false,
            Emphasis: null, Annotations: null);

    private static EditorBuffer? Show(
        GuiTestHarness h, DiffContentView view, string[] lines, bool editable = true)
    {
        var buffer = editable ? Buffer(lines) : null;
        view.SetRenderState(State(lines), buffer);
        h.Render();
        return buffer;
    }

    private static float TextOrigin(int digits) =>
        DiffRowPainter.LineTextOriginX(
            0f, digits * Advance + 8f, singleGutter: true, foldColumn: false, glyphColumn: false);

    private static float XOfCell(int cell, int digits = 1) => TextOrigin(digits) + cell * Advance;

    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static void ClickCell(GuiTestHarness h, int row, int cell, int digits = 1)
    {
        h.MoveTo(XOfCell(cell, digits), RowCenterY(row));
        h.Press();
        h.Release();
    }

    private static (int Row, int Cell)? Caret(GuiTestHarness h, int digits = 1)
    {
        var color = ThemeStyles.Dark.DiffContent.Caret;
        foreach (var r in h.Render().Rects)
        {
            var rect = r.Inputs.Position;
            if (r.Inputs.Style.BackgroundColor != color) continue;
            if (Math.Abs(rect.Width - 2f) > 0.01f) continue;
            return (
                (int)MathF.Round((Top - rect.Top) / RowH),
                (int)MathF.Round((rect.Left - TextOrigin(digits)) / Advance));
        }
        return null;
    }

    private static IReadOnlyList<RectF> SelectionRects(GuiTestHarness h)
    {
        var color = ThemeStyles.Dark.DiffContent.SelectionBackground;
        return h.Render().Rects
            .Where(r => r.Inputs.Style.BackgroundColor == color)
            .Select(r => r.Inputs.Position)
            .ToList();
    }

    private static RectF? CaretRect(GuiTestHarness h)
    {
        var color = ThemeStyles.Dark.DiffContent.Caret;
        foreach (var r in h.Render().Rects)
            if (r.Inputs.Style.BackgroundColor == color && Math.Abs(r.Inputs.Position.Width - 2f) < 0.01f)
                return r.Inputs.Position;
        return null;
    }

    [Fact]
    public void ClickOnATabbedLineLandsOnTheCharacterUnderThePointer()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["\tif (x)", "\treturn;"]);

            ClickCell(h, row: 0, cell: 5);
            Assert.Equal((0, 5), Caret(h));

            ClickCell(h, row: 0, cell: 3);
            Assert.Equal((0, 4), Caret(h));

            ClickCell(h, row: 0, cell: 1);
            Assert.Equal((0, 0), Caret(h));
        }
    }

    [Fact]
    public void ClickOnACjkLineLandsBetweenGlyphsRatherThanInsideOne()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["日本語x"]);

            ClickCell(h, row: 0, cell: 2);
            Assert.Equal((0, 2), Caret(h));

            ClickCell(h, row: 0, cell: 3);
            Assert.Equal((0, 4), Caret(h));

            ClickCell(h, row: 0, cell: 6);
            Assert.Equal((0, 6), Caret(h));
        }
    }

    [Fact]
    public void ClickOnACombiningMarkLandsPastTheWholeCluster()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["éx"]);

            ClickCell(h, row: 0, cell: 1);
            Assert.Equal((0, 1), Caret(h));

            h.PressKey(KeyboardKey.LeftArrow);
            Assert.Equal((0, 0), Caret(h));
            h.PressKey(KeyboardKey.RightArrow);
            Assert.Equal((0, 1), Caret(h));
        }
    }

    [Fact]
    public void GoalColumnSurvivesAPassThroughAShortLine()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["aaaaaaaaaa", "bb", "cccccccccc"]);

            ClickCell(h, row: 0, cell: 8);
            h.PressKey(KeyboardKey.DownArrow);
            Assert.Equal((1, 2), Caret(h));

            h.PressKey(KeyboardKey.DownArrow);
            Assert.Equal((2, 8), Caret(h));
        }
    }

    [Fact]
    public void AHorizontalMoveClearsTheGoalColumn()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["aaaaaaaaaa", "bb", "cccccccccc"]);

            ClickCell(h, row: 0, cell: 8);
            h.PressKey(KeyboardKey.DownArrow);
            h.PressKey(KeyboardKey.LeftArrow);
            h.PressKey(KeyboardKey.DownArrow);

            Assert.Equal((2, 1), Caret(h));
        }
    }

    [Fact]
    public void LeftWithALiveSelectionCollapsesToItsStart()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["abcdefghij"]);

            h.MoveTo(XOfCell(2), RowCenterY(0));
            h.Press();
            h.MoveTo(XOfCell(7), RowCenterY(0));
            h.Release();
            Assert.Equal((0, 7), Caret(h));

            h.PressKey(KeyboardKey.LeftArrow);
            Assert.Equal((0, 2), Caret(h));
        }
    }

    [Fact]
    public void RightWithALiveSelectionCollapsesToItsEnd()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["abcdefghij"]);

            h.MoveTo(XOfCell(7), RowCenterY(0));
            h.Press();
            h.MoveTo(XOfCell(2), RowCenterY(0));
            h.Release();

            h.PressKey(KeyboardKey.RightArrow);
            Assert.Equal((0, 7), Caret(h));
        }
    }

    [Fact]
    public void HomeTogglesBetweenTheFirstNonWhitespaceAndColumnZero()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["    value = 1;"]);

            ClickCell(h, row: 0, cell: 10);
            h.PressKey(KeyboardKey.Home);
            Assert.Equal((0, 4), Caret(h));

            h.PressKey(KeyboardKey.Home);
            Assert.Equal((0, 0), Caret(h));

            h.PressKey(KeyboardKey.Home);
            Assert.Equal((0, 4), Caret(h));
        }
    }

    [Fact]
    public void EndGoesToTheLastColumnOfTheLine()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["value = 1;"]);

            ClickCell(h, row: 0, cell: 2);
            h.PressKey(KeyboardKey.End);
            Assert.Equal((0, 10), Caret(h));
        }
    }

    [Fact]
    public void ShiftArrowExtendsTheSelectionRatherThanMovingIt()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["abcdefghij"]);

            ClickCell(h, row: 0, cell: 2);
            h.PressKey(KeyboardKey.RightArrow, InputModifiers.Shift);
            h.PressKey(KeyboardKey.RightArrow, InputModifiers.Shift);
            Assert.Equal((0, 4), Caret(h));

            var color = ThemeStyles.Dark.DiffContent.SelectionBackground;
            var rect = Assert.Single(h.Render().Rects.Where(r => r.Inputs.Style.BackgroundColor == color));
            Assert.Equal(XOfCell(2), rect.Inputs.Position.Left, 3);
            Assert.Equal(2 * Advance, rect.Inputs.Position.Width, 3);
        }
    }

    [Fact]
    public void TheWordModifierWithAnArrowMovesByWord()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["alpha beta gamma"]);

            ClickCell(h, row: 0, cell: 0);
            h.PressKey(KeyboardKey.RightArrow, Word);
            Assert.Equal((0, 6), Caret(h));

            h.PressKey(KeyboardKey.LeftArrow, Word);
            Assert.Equal((0, 0), Caret(h));
        }
    }

    [Fact]
    public void CtrlEndReachesTheEndOfTheDocumentAndScrollsToIt()
    {
        var (h, view, _) = Create();
        using (h)
        {
            var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
            Show(h, view, lines);

            ClickCell(h, row: 0, cell: 0, digits: 3);
            h.PressKey(KeyboardKey.End, Command);

            var rect = CaretRect(h);
            Assert.NotNull(rect);
            Assert.InRange(rect!.Value.Bottom, 0f, ViewHeight);

            h.PressKey(KeyboardKey.Home, Command);
            var back = CaretRect(h);
            Assert.NotNull(back);
            Assert.InRange(back!.Value.Bottom, 0f, ViewHeight);
        }
    }

    [Fact]
    public void ArrowingPastTheBottomEdgeScrollsTheCaretBackIntoView()
    {
        var (h, view, _) = Create();
        using (h)
        {
            var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
            Show(h, view, lines);

            ClickCell(h, row: 0, cell: 0, digits: 3);
            for (var i = 0; i < 60; i++) h.PressKey(KeyboardKey.DownArrow);

            var rect = CaretRect(h);
            Assert.NotNull(rect);
            Assert.InRange(rect!.Value.Bottom, 0f, ViewHeight);
        }
    }

    [Fact]
    public void EndOfALongLineScrollsTheCaretHorizontallyIntoView()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, [new string('x', 400)]);

            ClickCell(h, row: 0, cell: 0);
            h.PressKey(KeyboardKey.End);

            var rect = CaretRect(h);
            Assert.NotNull(rect);
            Assert.InRange(rect!.Value.Left, 0f, 800f);
        }
    }

    [Fact]
    public void PageDownAndPageUpMoveByAViewportAndComeBack()
    {
        var (h, view, _) = Create();
        using (h)
        {
            var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
            Show(h, view, lines);

            ClickCell(h, row: 0, cell: 0, digits: 3);
            h.PressKey(KeyboardKey.PageDown);
            var down = CaretRect(h);
            Assert.NotNull(down);

            h.PressKey(KeyboardKey.PageUp);
            Assert.Equal((0, 0), Caret(h, digits: 3));
        }
    }

    [Fact]
    public void TheCaretBlinksOffAndBackOn()
    {
        var (h, view, _) = Create();
        using (h)
        {
            Show(h, view, ["abcdef"]);
            ClickCell(h, row: 0, cell: 3);
            Assert.NotNull(CaretRect(h));

            h.Advance(0.6f);
            Assert.Null(CaretRect(h));

            h.Advance(0.6f);
            Assert.NotNull(CaretRect(h));
        }
    }

    [Fact]
    public void AReadOnlySurfaceDrawsNoCaretAndClaimsNoMotionKeys()
    {
        var (h, view, probe) = Create();
        using (h)
        {
            Show(h, view, ["alpha", "beta"], editable: false);

            ClickCell(h, row: 0, cell: 2);
            Assert.Null(CaretRect(h));

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.RightArrow);
            h.PressKey(KeyboardKey.DownArrow);
            h.PressKey(KeyboardKey.Home);
            h.PressKey(KeyboardKey.End);
            h.PressKey(KeyboardKey.PageDown);

            Assert.Equal(
                new[]
                {
                    KeyboardKey.RightArrow, KeyboardKey.DownArrow, KeyboardKey.Home,
                    KeyboardKey.End, KeyboardKey.PageDown,
                },
                probe.Seen);
            Assert.Null(CaretRect(h));
        }
    }

    [Fact]
    public void AnEditableSurfaceClaimsTheMotionKeysItMoves()
    {
        var (h, view, probe) = Create();
        using (h)
        {
            Show(h, view, ["alpha", "beta"]);
            ClickCell(h, row: 0, cell: 2);

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.RightArrow);
            h.PressKey(KeyboardKey.DownArrow);
            h.PressKey(KeyboardKey.Home);

            Assert.Empty(probe.Seen);
        }
    }

    [Fact]
    public void EscapeOnAnEditableBodyDropsTheRangeAndKeepsTypingReachingTheCaret()
    {
        var (h, view, probe) = Create();
        using (h)
        {
            var buffer = Show(h, view, ["abcdefghij"]);

            h.MoveTo(XOfCell(2), RowCenterY(0));
            h.Press();
            h.MoveTo(XOfCell(7), RowCenterY(0));
            h.Release();
            Assert.NotEmpty(SelectionRects(h));

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.Escape);

            Assert.Empty(SelectionRects(h));
            Assert.Equal((0, 7), Caret(h));
            Assert.Empty(probe.Seen);

            h.Type("X");
            Assert.Equal("abcdefgXhij", buffer!.Session.Document.Text);

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.Escape);
            Assert.Equal(new[] { KeyboardKey.Escape }, probe.Seen);
            Assert.Equal((0, 8), Caret(h));
        }
    }

    [Fact]
    public void EscapeOnAReadOnlyBodyStillClearsTheSelectionAndThenKeepsBubbling()
    {
        var (h, view, probe) = Create();
        using (h)
        {
            Show(h, view, ["abcdefghij"], editable: false);

            h.MoveTo(XOfCell(2), RowCenterY(0));
            h.Press();
            h.MoveTo(XOfCell(7), RowCenterY(0));
            h.Release();
            Assert.NotEmpty(SelectionRects(h));

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.Escape);
            Assert.Empty(SelectionRects(h));
            Assert.Empty(probe.Seen);

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.Escape);
            Assert.Equal(new[] { KeyboardKey.Escape }, probe.Seen);
        }
    }

    [Fact]
    public void AFileThatCannotBeWrittenBackIsNeverOpenedForEditing()
    {
        foreach (var reason in new[] { WriteBackRefusal.Truncated, WriteBackRefusal.Lossy })
            Assert.Null(EditorBuffer.TryOpen(
                "file.cs", FilePreviewFixture.Of(["alpha"]), new FileWriteBack.Refused(reason), highlight: null, Loc()));
    }

    [Fact]
    public void ARefusedFileShowsNoCaretAndClaimsNoKeys()
    {
        var (h, view, probe) = Create();
        using (h)
        {
            var refused = EditorBuffer.TryOpen(
                "file.cs", FilePreviewFixture.Of(["alpha", "beta"]),
                new FileWriteBack.Refused(WriteBackRefusal.Lossy), highlight: null, Loc());
            view.SetRenderState(State("alpha", "beta"), refused);
            h.Render();

            ClickCell(h, row: 0, cell: 2);
            Assert.Null(CaretRect(h));

            probe.Seen.Clear();
            h.PressKey(KeyboardKey.RightArrow);
            Assert.Equal(new[] { KeyboardKey.RightArrow }, probe.Seen);
        }
    }

    [Fact]
    public void ATrailingNewlineOpensARowTheViewerDoesNotShow()
    {
        var (h, view, _) = Create();
        using (h)
        {
            var buffer = Buffer(endsWithNewline: true, "alpha", "beta");
            view.SetRenderState(State("alpha", "beta"), buffer);
            h.Render();

            ClickCell(h, row: 2, cell: 0);
            Assert.Equal((2, 0), Caret(h));
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;
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
using ZGF.Gui.Views;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public class EditorTypingViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;
    private const float ViewHeight = 600f;
    private const float PaneWidth = 400f;

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

    private sealed class SidePane : KeyboardMouseController
    {
        public readonly List<KeyboardKey> Seen = new();

        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            if (e.State != InputState.Pressed) return;
            Seen.Add(e.Key);
            e.Consume();
        }
    }

    private sealed record Surface(
        GuiTestHarness Harness,
        DiffContentView View,
        KeyProbe Probe,
        SidePane Side,
        FakeClipboard Clipboard);

    private static Surface Create()
    {
        DiffContentView view = null!;
        RectView side = null!;
        var probe = new KeyProbe();
        var sidePane = new SidePane();
        var clipboard = new FakeClipboard();

        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx) { Width = PaneWidth };
                side = new RectView { Width = PaneWidth };
                var row = new RowView();
                row.Children.Add(view);
                row.Children.Add(side);
                return row;
            },
            width: (int)(PaneWidth * 2),
            height: (int)ViewHeight,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(clipboard);
            });

        harness.Input.RegisterController(view, probe, EventPhaseFilter.Bubble);
        harness.Input.RegisterController(side, sidePane, EventPhaseFilter.Both);
        return new Surface(harness, view, probe, sidePane, clipboard);
    }

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static EditorBuffer Buffer(string path, IReadOnlyList<string> lines) =>
        EditorBuffer.TryOpen(
            path,
            FilePreviewFixture.Of(lines, endsWithNewline: false),
            new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, false)),
            highlight: null,
            Loc())!;

    private static DiffRenderState.FullFile State(string path, IReadOnlyList<string> lines) =>
        new(path, lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false,
            Emphasis: null, Annotations: null);

    private static EditorBuffer? Show(
        Surface s, string[] lines, bool editable = true, string path = "file.cs")
    {
        var buffer = editable ? Buffer(path, lines) : null;
        s.View.SetRenderState(State(path, lines), buffer);
        s.Harness.Render();
        return buffer;
    }

    private static string Text(EditorBuffer? buffer) => buffer!.Session.Document.Text;

    private static float TextOrigin(int digits) =>
        DiffRowPainter.LineTextOriginX(
            0f, digits * Advance + 8f, singleGutter: true, foldColumn: false, glyphColumn: false);

    private static float XOfCell(int cell, int digits = 1) => TextOrigin(digits) + cell * Advance;

    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static void ClickCell(Surface s, int row, int cell, int digits = 1)
    {
        s.Harness.MoveTo(XOfCell(cell, digits), RowCenterY(row));
        s.Harness.Press();
        s.Harness.Release();
    }

    private static void DragCells(Surface s, int row, int from, int to, int digits = 1)
    {
        s.Harness.MoveTo(XOfCell(from, digits), RowCenterY(row));
        s.Harness.Press();
        s.Harness.MoveTo(XOfCell(to, digits), RowCenterY(row));
        s.Harness.Release();
    }

    private static (int Row, int Cell)? Caret(Surface s, int digits = 1)
    {
        var color = ThemeStyles.Dark.DiffContent.Caret;
        foreach (var r in s.Harness.Render().Rects)
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

    private static RectF? CaretRect(Surface s)
    {
        var color = ThemeStyles.Dark.DiffContent.Caret;
        foreach (var r in s.Harness.Render().Rects)
            if (r.Inputs.Style.BackgroundColor == color && Math.Abs(r.Inputs.Position.Width - 2f) < 0.01f)
                return r.Inputs.Position;
        return null;
    }

    private static IReadOnlyList<RectF> SelectionRects(Surface s)
    {
        var color = ThemeStyles.Dark.DiffContent.SelectionBackground;
        return s.Harness.Render().Rects
            .Where(r => r.Inputs.Style.BackgroundColor == color)
            .Select(r => r.Inputs.Position)
            .ToList();
    }

    [Fact]
    public void TypedCharactersReachTheDocument()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.Type("cd");

            Assert.Equal("abcd", Text(buffer));
            Assert.Equal((0, 4), Caret(s));
        }
    }

    [Fact]
    public void ACharacterWithNoKeyBehindItStillTypes()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["caf"]);

            ClickCell(s, row: 0, cell: 3);
            s.Harness.Type("\u00e9\u0439");

            Assert.Equal("caf\u00e9\u0439", Text(buffer));
        }
    }

    [Fact]
    public void APlainTextKeyIsClaimedAsTextAndFiresNoAppBinding()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one"]);

            ClickCell(s, row: 0, cell: 3);
            s.Probe.Seen.Clear();
            s.Harness.Type(" two");

            Assert.Equal("one two", Text(buffer));
            Assert.Empty(s.Probe.Seen);
        }
    }

    [Fact]
    public void AControlCharacterNeverReachesTheBuffer()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.SendText(new Rune('\t'));
            s.Harness.SendText(new Rune(7));
            s.Harness.SendText(new Rune('\n'));

            Assert.Equal("ab", Text(buffer));
        }
    }

    [Fact]
    public void ARunOfTypingIsOneUndoStepAndAPasteIsItsOwn()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            s.Clipboard.SetText("XY");

            ClickCell(s, row: 0, cell: 2);
            s.Harness.Type("cd");
            s.Harness.PressKey(KeyboardKey.V, Command);
            s.Harness.Type("ef");
            Assert.Equal("abcdXYef", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, Command);
            Assert.Equal("abcdXY", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, Command);
            Assert.Equal("abcd", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, Command);
            Assert.Equal("ab", Text(buffer));
        }
    }

    [Fact]
    public void RedoIsBothShiftCtrlZAndCtrlY()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.Type("cd");
            s.Harness.PressKey(KeyboardKey.Z, Command);
            Assert.Equal("ab", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, Command | InputModifiers.Shift);
            Assert.Equal("abcd", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, Command);
            s.Harness.PressKey(KeyboardKey.Y, Command);
            Assert.Equal("abcd", Text(buffer));
        }
    }

    [Fact]
    public void UndoRestoresTheSelectionRatherThanOnlyTheText()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["alpha beta"]);

            DragCells(s, row: 0, from: 6, to: 10);
            s.Harness.Type("x");
            Assert.Equal("alpha x", Text(buffer));
            Assert.Empty(SelectionRects(s));

            s.Harness.PressKey(KeyboardKey.Z, Command);

            Assert.Equal("alpha beta", Text(buffer));
            var rect = Assert.Single(SelectionRects(s));
            Assert.Equal(XOfCell(6), rect.Left, 3);
            Assert.Equal(4 * Advance, rect.Width, 3);
        }
    }

    [Fact]
    public void BackspaceAndDeleteTakeAClusterAndTheWordModifierTakesAWord()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["alpha beta"]);

            ClickCell(s, row: 0, cell: 10);
            s.Harness.PressKey(KeyboardKey.Backspace);
            Assert.Equal("alpha bet", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Backspace, Word);
            Assert.Equal("alpha ", Text(buffer));

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.Delete);
            Assert.Equal("lpha ", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Delete, Word);
            Assert.Equal("", Text(buffer));
        }
    }

    [Fact]
    public void BackspaceAtTheStartOfALineJoinsItToTheOneAbove()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one", "two"]);

            ClickCell(s, row: 1, cell: 0);
            s.Harness.PressKey(KeyboardKey.Backspace);

            Assert.Equal("onetwo", Text(buffer));
            Assert.Equal((0, 3), Caret(s));
        }
    }

    [Fact]
    public void EnterCarriesTheIndentationDown()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["    value = 1;"]);

            ClickCell(s, row: 0, cell: 14);
            s.Harness.PressKey(KeyboardKey.Enter);

            Assert.Equal("    value = 1;\n    ", Text(buffer));
            Assert.Equal((1, 4), Caret(s));
        }
    }

    [Fact]
    public void CtrlEnterKeepsBubblingToTheOwnersSubmit()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one"]);

            ClickCell(s, row: 0, cell: 3);
            s.Probe.Seen.Clear();
            s.Harness.PressKey(KeyboardKey.Enter, Command);

            Assert.Equal("one", Text(buffer));
            Assert.Equal(new[] { KeyboardKey.Enter }, s.Probe.Seen);
        }
    }

    [Fact]
    public void TabIndentsWholeLinesOverAMultiLineSelection()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one", "two", "three"]);

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.DownArrow, InputModifiers.Shift);
            s.Harness.PressKey(KeyboardKey.RightArrow, InputModifiers.Shift);
            s.Harness.PressKey(KeyboardKey.Tab);
            Assert.Equal("    one\n    two\nthree", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Tab, InputModifiers.Shift);
            Assert.Equal("one\ntwo\nthree", Text(buffer));
        }
    }

    [Fact]
    public void TabWithNoSelectionIndentsToTheNextStopAndShiftTabTakesNothingFromAnUnindentedLine()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.PressKey(KeyboardKey.Tab);
            Assert.Equal("ab  ", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Tab, InputModifiers.Shift);
            Assert.Equal("ab  ", Text(buffer));
        }
    }

    [Fact]
    public void TabIndentsWithTabsInAFileThatIsIndentedWithThem()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["class C", "\tvoid M()", "\t{", "\t}"]);

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.Tab);

            Assert.StartsWith("\tclass C", Text(buffer));
        }
    }

    [Fact]
    public void CtrlSlashCommentsAndUncommentsTheSelectedLines()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one();", "two();"]);

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.DownArrow, InputModifiers.Shift);
            s.Harness.PressKey(KeyboardKey.RightArrow, InputModifiers.Shift);
            s.Harness.PressKey(KeyboardKey.Slash, Command);
            Assert.Equal("// one();\n// two();", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Slash, Command);
            Assert.Equal("one();\ntwo();", Text(buffer));
        }
    }

    [Fact]
    public void ALanguageWithNoLineCommentDeclines()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["<p>hi</p>"], path: "page.html");

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.Slash, Command);

            Assert.Equal("<p>hi</p>", Text(buffer));
        }
    }

    [Fact]
    public void CutTakesTheSelectionToTheClipboardAndPasteBringsItBack()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["alpha beta"]);

            DragCells(s, row: 0, from: 6, to: 10);
            s.Harness.PressKey(KeyboardKey.X, Command);
            Assert.Equal("beta", s.Clipboard.Text);
            Assert.Equal("alpha ", Text(buffer));

            s.Harness.PressKey(KeyboardKey.V, Command);
            Assert.Equal("alpha beta", Text(buffer));
        }
    }

    [Fact]
    public void CopyLeavesTheDocumentAloneAndSelectAllTakesTheWholeFile()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one", "two"]);

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.A, Command);
            s.Harness.PressKey(KeyboardKey.C, Command);

            Assert.Equal("one\ntwo", s.Clipboard.Text);
            Assert.Equal("one\ntwo", Text(buffer));
        }
    }

    [Fact]
    public void PastingManyLinesIsOneStepAndLeavesTheCaretInView()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["head", "tail"]);
            s.Clipboard.SetText(string.Join('\n', Enumerable.Range(1, 100).Select(i => $"p{i}")));

            ClickCell(s, row: 0, cell: 4);
            s.Harness.PressKey(KeyboardKey.V, Command);

            Assert.StartsWith("headp1\np2\n", Text(buffer));
            Assert.EndsWith("p100\ntail", Text(buffer));

            var rect = CaretRect(s);
            Assert.NotNull(rect);
            Assert.InRange(rect!.Value.Bottom, 0f, ViewHeight);

            s.Harness.PressKey(KeyboardKey.Z, Command);
            Assert.Equal("head\ntail", Text(buffer));
        }
    }

    [Fact]
    public void PastedLineEndingsBecomeTheDocumentsOwn()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["x"]);
            s.Clipboard.SetText("a\r\nb\rc");

            ClickCell(s, row: 0, cell: 0);
            s.Harness.PressKey(KeyboardKey.V, Command);

            Assert.Equal("a\nb\ncx", Text(buffer));
        }
    }

    [Fact]
    public void TypingPastTheBottomEdgeScrollsTheCaretBackIntoView()
    {
        var s = Create();
        using (s.Harness)
        {
            var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
            var buffer = Show(s, lines);

            ClickCell(s, row: 0, cell: 0, digits: 3);
            for (var i = 0; i < 60; i++) s.Harness.PressKey(KeyboardKey.Enter);

            Assert.Equal(260, buffer!.Session.Document.LineCount);
            var rect = CaretRect(s);
            Assert.NotNull(rect);
            Assert.InRange(rect!.Value.Bottom, 0f, ViewHeight);
        }
    }

    [Fact]
    public void TheKeysGoToTheCaretWhereverThePointerHasWanderedTo()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["alpha", "beta", "gamma"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.MoveTo(PaneWidth + 100f, 300f);
            s.Side.Seen.Clear();

            s.Harness.PressKey(KeyboardKey.DownArrow);

            Assert.Equal((1, 2), Caret(s));
            Assert.Empty(s.Side.Seen);
        }
    }

    [Fact]
    public void TypingWhileThePointerIsOverAnotherPaneStillTypes()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.MoveTo(PaneWidth + 100f, 300f);
            s.Side.Seen.Clear();

            s.Harness.Type("c");

            Assert.Equal("abc", Text(buffer));
            Assert.Empty(s.Side.Seen);
        }
    }

    [Fact]
    public void AReadOnlyBodyLeavesTheArrowToThePaneThePointerIsOver()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["alpha", "beta"], editable: false);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.MoveTo(PaneWidth + 100f, 300f);
            s.Side.Seen.Clear();

            s.Harness.PressKey(KeyboardKey.DownArrow);

            Assert.Equal(new[] { KeyboardKey.DownArrow }, s.Side.Seen);
        }
    }

    [Fact]
    public void AReadOnlyBodyClaimsNoEditingKeyAndTypesNothing()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["alpha", "beta"], editable: false);

            ClickCell(s, row: 0, cell: 2);
            s.Probe.Seen.Clear();

            s.Harness.PressKey(KeyboardKey.Backspace);
            s.Harness.PressKey(KeyboardKey.Delete);
            s.Harness.PressKey(KeyboardKey.Enter);
            s.Harness.PressKey(KeyboardKey.Tab);
            s.Harness.PressKey(KeyboardKey.V, Command);
            s.Harness.PressKey(KeyboardKey.Z, Command);
            s.Harness.PressKey(KeyboardKey.Y, Command);
            s.Harness.PressKey(KeyboardKey.Slash, Command);
            s.Harness.Type("x");

            Assert.Equal(
                new[]
                {
                    KeyboardKey.Backspace, KeyboardKey.Delete, KeyboardKey.Enter, KeyboardKey.Tab,
                    KeyboardKey.V, KeyboardKey.Z, KeyboardKey.Y, KeyboardKey.Slash, KeyboardKey.X,
                },
                s.Probe.Seen);
            Assert.Null(CaretRect(s));
        }
    }

    [Fact]
    public void AnEditableSelectionSurvivesAClickInAnotherPaneAndItsCaretStopsBeingDrawn()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["alpha", "beta"]);
            DragCells(s, row: 0, from: 1, to: 4);
            Assert.NotEmpty(SelectionRects(s));
            Assert.NotNull(CaretRect(s));

            s.Harness.MoveTo(PaneWidth + 100f, 300f);
            s.Harness.Press();
            s.Harness.Release();

            Assert.NotEmpty(SelectionRects(s));
            Assert.Null(CaretRect(s));

            var viewer = Create();
            using (viewer.Harness)
            {
                Show(viewer, ["alpha", "beta"], editable: false);
                DragCells(viewer, row: 0, from: 1, to: 4);
                Assert.NotEmpty(SelectionRects(viewer));

                viewer.Harness.MoveTo(PaneWidth + 100f, 300f);
                viewer.Harness.Press();
                viewer.Harness.Release();

                Assert.Empty(SelectionRects(viewer));
            }
        }
    }

    [Fact]
    public void AnEditedFileGrowsAndShrinksTheRowsTheListDraws()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["one"]);

            ClickCell(s, row: 0, cell: 3);
            s.Harness.PressKey(KeyboardKey.Enter);
            s.Harness.Type("two");
            Assert.Equal("one\ntwo", Text(buffer));
            Assert.Equal((1, 3), Caret(s));

            s.Harness.MoveTo(XOfCell(1), RowCenterY(1));
            s.Harness.Press();
            s.Harness.Release();
            Assert.Equal((1, 1), Caret(s));
        }
    }
}

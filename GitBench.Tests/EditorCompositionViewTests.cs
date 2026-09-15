using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Desktop.Input;
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

public class EditorCompositionViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;
    private const float ViewHeight = 600f;
    private const float PaneWidth = 400f;

    private sealed class FakeImeHost : IImeHost
    {
        public bool Enabled;
        public int EnabledChanges;
        public int Resets;
        public RectF? CaretRect;

        public void SetImeEnabled(bool enabled)
        {
            if (Enabled != enabled) EnabledChanges++;
            Enabled = enabled;
        }

        public void SetImeCaretRect(RectF caretRect) => CaretRect = caretRect;

        public void ResetComposition() => Resets++;
    }

    private sealed record Surface(
        GuiTestHarness Harness, DiffContentView View, KeyProbe Probe, FakeImeHost Ime);

    private static Surface Create()
    {
        DiffContentView view = null!;
        var probe = new KeyProbe();
        var clipboard = new FakeClipboard();

        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx) { Width = PaneWidth };
                var row = new RowView();
                row.Children.Add(view);
                row.Children.Add(new RectView { Width = PaneWidth });
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
        var ime = new FakeImeHost();
        harness.Input.ImeHost = ime;
        return new Surface(harness, view, probe, ime);
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

    private static EditorBuffer Show(Surface s, string[] lines, string path = "file.cs")
    {
        var buffer = Buffer(path, lines);
        s.View.SetRenderState(State(path, lines), buffer);
        s.Harness.Render();
        return buffer;
    }

    private static string Text(EditorBuffer buffer) => buffer.Session.Document.Text;

    private static float TextOrigin(int digits = 1) =>
        DiffRowPainter.LineTextOriginX(
            0f, digits * Advance + 8f, singleGutter: true, foldColumn: false, glyphColumn: false);

    private static float XOfCell(int cell) => TextOrigin() + cell * Advance;

    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static void ClickCell(Surface s, int row, int cell)
    {
        s.Harness.MoveTo(XOfCell(cell), RowCenterY(row));
        s.Harness.Press();
        s.Harness.Release();
    }

    private static void DragCells(Surface s, int row, int from, int to)
    {
        s.Harness.MoveTo(XOfCell(from), RowCenterY(row));
        s.Harness.Press();
        s.Harness.MoveTo(XOfCell(to), RowCenterY(row));
        s.Harness.Release();
    }

    private static void ClickAway(Surface s)
    {
        s.Harness.MoveTo(PaneWidth + 40f, Top - 40f);
        s.Harness.Press();
        s.Harness.Release();
    }

    private static string Drawn(Surface s) =>
        string.Concat(s.Harness.Render().Texts.Select(t => t.Inputs.Text));

    private static (int Row, int Cell)? Caret(Surface s)
    {
        var color = ThemeStyles.Dark.DiffContent.Caret;
        foreach (var r in s.Harness.Render().Rects)
        {
            var rect = r.Inputs.Position;
            if (r.Inputs.Style.BackgroundColor != color) continue;
            if (Math.Abs(rect.Width - 2f) > 0.01f) continue;
            return ((int)MathF.Round((Top - rect.Top) / RowH),
                (int)MathF.Round((rect.Left - TextOrigin()) / Advance));
        }
        return null;
    }

    private static IReadOnlyList<(int From, int To, float Thickness)> Underlines(Surface s)
    {
        var color = ThemeStyles.Dark.DiffContent.LineText;
        return s.Harness.Render().Lines
            .Where(l => l.Inputs.Color == color)
            .Select(l => (
                (int)MathF.Round((l.Inputs.Start.X - TextOrigin()) / Advance),
                (int)MathF.Round((l.Inputs.End.X - TextOrigin()) / Advance),
                l.Inputs.Thickness))
            .ToList();
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
    public void ComposingLeavesTheDocumentAndItsRevisionAlone()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);
            var revision = buffer.Document.Revision;

            s.Harness.SendComposition("ni");
            s.Harness.SendComposition("nih");
            s.Harness.SendComposition("你好");

            Assert.Equal("ab", Text(buffer));
            Assert.Equal(revision, buffer.Document.Revision);
            Assert.True(buffer.ReadIsCurrent);
        }
    }

    [Fact]
    public void TheCompositionIsDrawnAtTheCaretAndTheDocumentIsNot()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);

            s.Harness.SendComposition("你好");

            Assert.Contains("ab你好", Drawn(s));
            Assert.Equal("ab", Text(buffer));
        }
    }

    [Fact]
    public void TheMeasuredSpanIsTheDrawnSpan()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);

            s.Harness.SendComposition("你");

            Assert.Equal([(2, 4, 2f)], Underlines(s));
            Assert.Equal((0, 4), Caret(s));
        }
    }

    [Fact]
    public void TheCaretSitsWhereTheImeReportsItInsideTheComposition()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);

            s.Harness.SendComposition(
                "你好", caret: 1,
                blocks: [new PreeditBlock(0, 1), new PreeditBlock(1, 1)], focusedBlock: 0);

            Assert.Equal((0, 4), Caret(s));
            Assert.Equal([(2, 4, 2f), (4, 6, 1f)], Underlines(s));
        }
    }

    [Fact]
    public void ACommittedCompositionInsertsOnceAsOneUndoStep()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);

            s.Harness.Compose("nihao", "你好");

            Assert.Equal("ab你好", Text(buffer));
            Assert.Equal((0, 6), Caret(s));

            s.Harness.PressKey(KeyboardKey.Z, ShortcutModifier);
            Assert.Equal("ab", Text(buffer));
        }
    }

    [Fact]
    public void BlurDiscardsRatherThanCommits()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);
            s.Harness.SendComposition("你好");

            ClickAway(s);

            Assert.Equal(1, s.Ime.Resets);
            Assert.Equal("ab", Text(buffer));
            Assert.True(buffer.ReadIsCurrent);
            Assert.DoesNotContain("你", Drawn(s));
            Assert.False(s.Ime.Enabled);
        }
    }

    [Fact]
    public void CompositionOverASelectionReplacesItOnCommitNotOnStart()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["abcd"]);
            DragCells(s, row: 0, from: 1, to: 3);

            s.Harness.SendComposition("ni");
            Assert.Equal("abcd", Text(buffer));
            Assert.NotEmpty(SelectionRects(s));

            s.Harness.EndComposition();
            s.Harness.Type("你");

            Assert.Equal("a你d", Text(buffer));
        }
    }

    [Fact]
    public void KeysWhileComposingNeitherEditNorReachTheApp()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);
            s.Harness.SendComposition("ni");
            s.Probe.Seen.Clear();

            s.Harness.PressKey(KeyboardKey.Enter);
            s.Harness.PressKey(KeyboardKey.Space);
            s.Harness.PressKey(KeyboardKey.DownArrow);
            s.Harness.PressKey(KeyboardKey.Backspace);

            Assert.Equal("ab", Text(buffer));
            Assert.Empty(s.Probe.Seen);
        }
    }

    [Fact]
    public void TheImeIsOnOnlyWhileThereIsACaretToTypeInto()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["ab"]);
            Assert.False(s.Ime.Enabled);

            ClickCell(s, row: 0, cell: 2);
            s.Harness.Render();
            Assert.True(s.Ime.Enabled);

            ClickAway(s);
            Assert.False(s.Ime.Enabled);

            ClickCell(s, row: 0, cell: 1);
            s.Harness.Render();
            Assert.True(s.Ime.Enabled);
        }
    }

    [Fact]
    public void TheCandidateWindowIsAnchoredOnTheCompositionsOwnCaret()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);
            s.Harness.Render();
            var atCaret = s.Ime.CaretRect;

            s.Harness.SendComposition("你");

            Assert.NotNull(atCaret);
            Assert.NotNull(s.Ime.CaretRect);
            Assert.Equal(atCaret!.Value.Left + 2 * Advance, s.Ime.CaretRect!.Value.Left, 3);
        }
    }

    [Fact]
    public void AViewerNeitherComposesNorSwallowsTheEvent()
    {
        var s = Create();
        using (s.Harness)
        {
            s.View.SetRenderState(State("file.cs", ["ab"]), document: null);
            s.Harness.Render();
            ClickCell(s, row: 0, cell: 2);

            s.Harness.SendComposition("ni");
            s.Harness.Render();

            Assert.False(s.Ime.Enabled);
            Assert.DoesNotContain("abni", Drawn(s));
        }
    }

    [Fact]
    public void ClosingTheFileDiscardsTheCompositionAtOnce()
    {
        var s = Create();
        using (s.Harness)
        {
            Show(s, ["ab"]);
            ClickCell(s, row: 0, cell: 2);
            s.Harness.SendComposition("你");

            s.View.SetRenderState(new DiffRenderState.Placeholder("Select a file to view diff."), null);

            Assert.Equal(1, s.Ime.Resets);
            Assert.False(s.Ime.Enabled);
        }
    }

    [Fact]
    public void AClickElsewhereAbandonsTheCompositionRatherThanCommittingIt()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["abcd"]);
            ClickCell(s, row: 0, cell: 4);
            s.Harness.SendComposition("你");

            ClickCell(s, row: 0, cell: 1);

            Assert.Equal(1, s.Ime.Resets);
            Assert.Equal("abcd", Text(buffer));
            Assert.Equal("abcd", string.Concat(Drawn(s).Where(char.IsLetter)));
        }
    }

    [Fact]
    public void ACommitOverASelectionIsOneUndo()
    {
        var s = Create();
        using (s.Harness)
        {
            var buffer = Show(s, ["abcd"]);
            DragCells(s, row: 0, from: 1, to: 3);
            s.Harness.Compose("nihao", "你好");
            Assert.Equal("a你好d", Text(buffer));

            s.Harness.PressKey(KeyboardKey.Z, ShortcutModifier);
            Assert.Equal("abcd", Text(buffer));
        }
    }

    private static readonly InputModifiers ShortcutModifier =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX)
            ? InputModifiers.Super
            : InputModifiers.Control;
}

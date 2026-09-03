using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
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

// Shift+F12 on the real content view, through the real input system: the pointer decides what the
// question is about, exactly as it does for the F12 beside it.
public class UsagesShortcutTests
{
    private const string Root = "/repo";
    private const string File = "/repo/src/main.rs";

    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;

    // Asked at the identifier's own first column rather than wherever in it the pointer landed —
    // the same position the go-to-definition beside it asks at.
    [Fact]
    public void ShiftF12AsksWhereTheSymbolUnderThePointerIsUsed()
    {
        using var h = Create(out var presenter);

        Hover(h, XOfColumn(5), RowCenterY(1));
        h.PressKey(KeyboardKey.F12, InputModifiers.Shift);

        var asked = Assert.Single(presenter.Asked);
        Assert.Equal(10, asked.Line.Value);
        Assert.Equal(4, asked.Column.Value);
    }

    // The pointer is between words, so there is no identifier to name; the caret position is still
    // a place a server can answer about.
    [Fact]
    public void ShiftF12OffAnIdentifierAsksAtTheCaretPosition()
    {
        using var h = Create(out var presenter);

        Hover(h, XOfColumn(3), RowCenterY(1));
        h.PressKey(KeyboardKey.F12, InputModifiers.Shift);

        Assert.Equal(10, Assert.Single(presenter.Asked).Line.Value);
    }

    // F12 is go-to-definition and stays go-to-definition.
    [Fact]
    public void F12WithoutShiftAsksNothingAboutUsages()
    {
        using var h = Create(out var presenter);

        Hover(h, XOfColumn(5), RowCenterY(1));
        h.PressKey(KeyboardKey.F12);

        Assert.Empty(presenter.Asked);
    }

    // The key belongs to whatever the pointer is over, and it is over nothing here.
    [Fact]
    public void ShiftF12WithThePointerOutsideTheViewAsksNothing()
    {
        using var h = Create(out var presenter);

        h.PressKey(KeyboardKey.F12, InputModifiers.Shift);

        Assert.Empty(presenter.Asked);
    }

    // One hunk ten lines in. Rows: [0] top bar, [1] "var alpha = 1;", [2] tab-indented, [3], [4] EOF.
    private static DiffResult Diff()
    {
        var hunk = new DiffHunk(10, 3, 10, 3, null, new[]
        {
            new DiffLine(DiffLineKind.Context, 10, 10, "var alpha = 1;"),
            new DiffLine(DiffLineKind.Context, 11, 11, "\tCompute(alpha);"),
            new DiffLine(DiffLineKind.Context, 12, 12, "return alpha;"),
        });
        return new DiffResult(
            RepoId: Guid.Empty,
            Path: "file.cs",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: new[] { hunk },
            Truncated: false,
            ErrorMessage: null);
    }

    private static GuiTestHarness Create(out FakePresenter presenter)
    {
        var asked = new FakePresenter();
        DiffContentView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                var input = ctx.Require<InputSystem>();
                view.UseController(input, () => new DefinitionProbeController(
                    view,
                    new NoDefinitions(),
                    new NoNavigation(),
                    new ImmediateDispatcher(),
                    () => (Root, File),
                    () => input.Modifiers,
                    (_, _) => Task.CompletedTask,
                    asked), EventPhaseFilter.Capture);
                return view;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
            });
        view.SetRenderState(new DiffRenderState.Loaded(Diff()));
        harness.Render(); // resolve font metrics
        presenter = asked;
        return harness;
    }

    // Twice: the first move is what makes the view hovered, and only a controller already on the
    // hover path is handed the move that would tell it where the pointer is.
    private static void Hover(GuiTestHarness harness, float x, float y)
    {
        harness.MoveTo(x, y);
        harness.MoveTo(x, y);
    }

    private static float XOfColumn(int column)
    {
        var gutter = 2 * Advance + 8f; // two-digit line numbers
        return DiffRowPainter.LineTextOriginX(0f, gutter, singleGutter: false) + column * Advance;
    }

    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private sealed class FakePresenter : IUsagesPresenter
    {
        public List<(PointF Anchor, FileLine Line, RawColumn Column)> Asked { get; } = [];

        public void ShowUsagesOf(PointF anchor, FileLine line, RawColumn column) =>
            Asked.Add((anchor, line, column));
    }

    private sealed class NoDefinitions : IDefinitionSource
    {
        public bool CanDefine(string absolutePath) => false;

        public Task<DefinitionReply> DefineAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct) =>
            Task.FromResult(DefinitionReply.Nothing);
    }

    private sealed class NoNavigation : IFileNavigator
    {
        public void NavigateTo(string absolutePath, int line) { }

        public void GoBack() { }

        public void GoForward() { }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

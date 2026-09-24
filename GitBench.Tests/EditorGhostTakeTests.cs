using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>Taking a suggestion into the file: an insertion goes in after the line it hangs from, a
/// replacement goes in place of the lines it covers — wherever edits above have moved them.</summary>
public sealed class EditorGhostTakeTests
{
    private const string Path = "file.cs";

    private static (GuiTestHarness Harness, DiffContentView View, EditorBuffer Buffer) Show(params string[] lines) =>
        Show(null, lines);

    private static (GuiTestHarness Harness, DiffContentView View, EditorBuffer Buffer) Show(IUiDispatcher? dispatcher, string[] lines)
    {
        DiffContentView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                if (dispatcher is not null) ctx.AddService(dispatcher);
            });
        var buffer = EditorBuffer.TryOpen(
            Path,
            FilePreviewFixture.Of(lines, false),
            new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, false)),
            highlight: null,
            new LocalizationService(new State<Locale>(Locale.En)))!;
        view.SetRenderState(
            new DiffRenderState.FullFile(Path, lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false, Emphasis: null, Annotations: null),
            buffer);
        harness.Render();
        return (harness, view, buffer);
    }

    private static string[] Lines(EditorBuffer buffer) =>
        Enumerable.Range(1, buffer.Document.LineCount).Select(n => buffer.Document.Line(new FileLine(n))).ToArray();

    [Fact]
    public void AReplacement_GoesInPlaceOfTheLinesItCovers()
    {
        var (_, view, buffer) = Show("a", "b", "c", "d");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(3)), ["X", "Y", "Z"])));

        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["a", "X", "Y", "Z", "d"], Lines(buffer));
    }

    [Fact]
    public void AReplacement_FollowsTheLinesItCovers_WhenEditsAboveMoveThem()
    {
        var (_, view, buffer) = Show("a", "b", "c", "d");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(3)), ["X"])));

        buffer.Session.Paste(SelectionRange.At(TextPosition.At(1, 0)), "top\n");
        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["top", "a", "X", "d"], Lines(buffer));
    }

    [Fact]
    public void ADeletion_TakesTheLinesOut_LineBreaksAndAll()
    {
        var (_, view, buffer) = Show("a", "b", "c", "d");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(3)), [])));

        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["a", "d"], Lines(buffer));
    }

    [Fact]
    public void ADeletion_AtTheEndOfTheFile_TakesTheLineBreakBeforeIt()
    {
        var (_, view, buffer) = Show("a", "b", "c");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(3)), [])));

        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["a"], Lines(buffer));
    }

    [Fact]
    public void ADeletion_CarriesItsPills_OnTheFirstLineItTakesOut()
    {
        var (harness, view, _) = Show("a", "b", "c", "d");
        var ran = new List<string>();
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(3)), []),
            new SuggestionActions(() => ran.Add("accept"), () => ran.Add("accept and next"))));

        var accept = Assert.Single(harness.Render().Texts, t => t.Inputs.Text == "Accept").Inputs.Position;
        harness.Click(accept.Center.X, accept.Center.Y);

        Assert.Equal(["accept"], ran);
    }

    [Fact]
    public void AnInsertion_GoesInAfterTheLineItHangsFrom()
    {
        var (_, view, buffer) = Show("a", "b", "c");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X", "Y"])));

        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["a", "b", "X", "Y", "c"], Lines(buffer));
    }

    [Fact]
    public void AnInsertion_HangsFromItsLineWhole_WhenLinesBelowReadLikeIt()
    {
        var (_, view, buffer) = Show("function A() {", "  return (", "    <Text>", "    </Text>", "  );", "}", "function B() {", "  return (", "    <Text>", "    </Text>", "  );", "}");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(6)),
            ["function C() {", "  return (", "    <Text>", "    </Text>", "  );", "}"])));

        Assert.Equal(new FileLine(6), buffer.Rows.Ghost!.After);
        Assert.Equal(6, buffer.Rows.Ghost.Lines.Count);

        Assert.True(view.TakeGhost(Path));
        Assert.Equal("function C() {", buffer.Document.Line(new FileLine(7)));
        Assert.Equal(18, buffer.Document.LineCount);
    }

    [Fact]
    public void AnInsertion_Shrinks_AsTheReaderTypesIt()
    {
        var dispatcher = new QueuedDispatcher();
        var (_, view, buffer) = Show(dispatcher, ["a", "b", "Y", "c"]);
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X", "Y"])));

        buffer.Session.Paste(SelectionRange.At(TextPosition.At(2, 1)), "\nX");
        dispatcher.Drain();

        Assert.Equal(new FileLine(3), buffer.Rows.Ghost!.After);
        Assert.Equal(["Y"], buffer.Rows.Ghost.Lines);
    }

    [Fact]
    public void ASuggestionRecolored_KeepsWhereItHangs_AndColorsTheLinesLeft()
    {
        var dispatcher = new QueuedDispatcher();
        var (_, view, buffer) = Show(dispatcher, ["a", "b", "Y", "c"]);
        var ghost = new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X", "Y"]);
        view.SetHints(new EditorHints(Path, ghost));
        buffer.Session.Paste(SelectionRange.At(TextPosition.At(2, 1)), "\nX");
        dispatcher.Drain();

        TokenSpan[] y = [new(0, 1, TokenColorSlot.Keyword)];
        view.SetHints(new EditorHints(Path, ghost with { Spans = [[new TokenSpan(0, 1, TokenColorSlot.String)], y] }));

        Assert.Equal(new FileLine(3), buffer.Rows.Ghost!.After);
        Assert.Equal(y, Assert.Single(buffer.Rows.Rows.OfType<DiffRow.Ghost>()).Spans);
    }

    [Fact]
    public void AReplacement_ShowsTheCharactersThatChange_OnTheLineItReplaces()
    {
        var (_, view, buffer) = Show("a", "int count = 1;", "d");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(2), new FileLine(2)), ["int total = 1;"])));

        var ghost = Assert.Single(buffer.Rows.Rows.OfType<DiffRow.Ghost>());
        Assert.Equal([new CharRange(4, 5)], ghost.Emphasis);
    }

    [Fact]
    public void TheSuggestionsPills_OnItsFirstRow_RunTheirActions()
    {
        var (harness, view, _) = Show("a", "b", "c");
        var ran = new List<string>();
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X", "Y"]),
            new SuggestionActions(() => ran.Add("accept"), () => ran.Add("accept and next"))));

        var texts = harness.Render().Texts;
        var accept = Assert.Single(texts, t => t.Inputs.Text == "Accept").Inputs.Position;
        var next = Assert.Single(texts, t => t.Inputs.Text == "Accept & next").Inputs.Position;
        harness.Click(accept.Center.X, accept.Center.Y);
        harness.Click(next.Center.X, next.Center.Y);

        Assert.Equal(["accept", "accept and next"], ran);
    }

    [Fact]
    public void ASuggestionWithNoAccept_HasNoPill()
    {
        var (harness, view, _) = Show("a", "b", "c");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X"])));

        Assert.DoesNotContain(harness.Render().Texts, t => t.Inputs.Text == "Accept");
    }

    [Fact]
    public void ASuggestionOverAnotherFile_IsNotTaken()
    {
        var (_, view, buffer) = Show("a", "b");
        view.SetHints(new EditorHints("other.cs", new EditorGhost(new GhostPlace.Insert(new FileLine(1)), ["X"])));

        Assert.False(view.TakeGhost("other.cs"));

        Assert.Equal(["a", "b"], Lines(buffer));
    }
}

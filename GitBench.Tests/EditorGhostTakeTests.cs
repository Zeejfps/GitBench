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

    private static (GuiTestHarness Harness, DiffContentView View, EditorBuffer Buffer) Show(params string[] lines)
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
    public void AnInsertion_GoesInAfterTheLineItHangsFrom()
    {
        var (_, view, buffer) = Show("a", "b", "c");
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(2)), ["X", "Y"])));

        Assert.True(view.TakeGhost(Path));

        Assert.Equal(["a", "b", "X", "Y", "c"], Lines(buffer));
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
    public void ASuggestionOverAnotherFile_IsNotTaken()
    {
        var (_, view, buffer) = Show("a", "b");
        view.SetHints(new EditorHints("other.cs", new EditorGhost(new GhostPlace.Insert(new FileLine(1)), ["X"])));

        Assert.False(view.TakeGhost("other.cs"));

        Assert.Equal(["a", "b"], Lines(buffer));
    }
}

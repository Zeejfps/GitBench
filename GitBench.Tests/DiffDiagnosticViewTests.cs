using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Lsp;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Squiggles as the file's own viewer draws them, and the one thing that decides whether they are
/// drawn at all: whether the read the server was told about still describes what is on screen.
/// </summary>
/// <remarks>
/// A wave of diagnostics that arrives and is then not drawn is indistinguishable, from the reader's
/// side, from a server that never answered — which is what made this worth pinning at the surface
/// rather than only over the overlay's arithmetic.
/// </remarks>
public sealed class DiffDiagnosticViewTests
{
    private const string Path = "/repo/src/Greeter.cs";

    private static readonly string[] Lines =
    [
        "public class Greeter",
        "{",
        "    wdawd;",
        "}",
    ];

    [Fact]
    public void TheServersSquigglesAreDrawnOverTheLineItNamed()
    {
        using var view = Showing(out var buffer);

        view.Content.SetDiagnostics(Overlay());

        Assert.NotEmpty(Squiggles(view));
    }

    /// <summary>
    /// Deliberate: a diagnostic names a position in the text the server was given, and the moment
    /// something is typed those positions describe a file nobody has any more. A squiggle left
    /// under the previous text points at the wrong characters.
    /// </summary>
    [Fact]
    public void SquigglesGoAwayWhileThereAreUnsavedEdits()
    {
        using var view = Showing(out var buffer);
        view.Content.SetDiagnostics(Overlay());

        Type(buffer, "x");

        Assert.Empty(Squiggles(view));
    }

    /// <summary>
    /// The regression. Saving re-reads the file, and the read that comes back describes the
    /// document again — so what the server said about it is worth drawing again. Gated on the
    /// revision the buffer opened at instead, one keystroke hid every squiggle in the file for as
    /// long as it stayed open, saving and waiting included.
    /// </summary>
    [Fact]
    public void SquigglesComeBackOnceTheFileHasBeenSavedAndReadAgain()
    {
        using var view = Showing(out var buffer);
        view.Content.SetDiagnostics(Overlay());
        Type(buffer, "x");

        buffer.Reread();

        Assert.NotEmpty(Squiggles(view));
    }

    /// <summary>Every wave the drawn rows got underlined for, at the error color.</summary>
    private static IReadOnlyList<RecordedLine> Squiggles(Shown view)
    {
        var underline = view.Styles.Value.DiffContent.DiagnosticError;
        return [.. view.Harness.Render().Lines.Where(line => line.Inputs.Color == underline)];
    }

    private static DiffDiagnosticOverlay Overlay() =>
        new(Path, [
            new Diagnostic(
                new LspRange(
                    new LspPosition(new LspLine(2), new LspCharacter(4)),
                    new LspPosition(new LspLine(2), new LspCharacter(9))),
                DiagnosticSeverity.Error,
                "the type or namespace name 'wdawd' could not be found"),
        ]);

    private static void Type(EditorBuffer buffer, string text) =>
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), text);

    /// <summary>The file open in the pane the way the Files tab opens it: whole-file, and editable.</summary>
    private static Shown Showing(out EditorBuffer buffer)
    {
        var themes = new ThemeService(new State<ThemeMode>(ThemeMode.Dark));
        var loc = new LocalizationService(new State<Locale>(Locale.En));

        DiffContentView content = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                content = new DiffContentView(ctx);
                return content;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(themes);
                ctx.AddService<ILocalizationService>(loc);
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new NoopPlatformShell());
            });

        buffer = EditorBuffer.TryOpen(
            Path,
            FilePreviewFixture.Of(Lines),
            FilePreviewFixture.Reversible,
            highlight: null,
            loc)!;

        content.SetRenderState(
            new DiffRenderState.FullFile(
                Path, Lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false),
            buffer);

        return new Shown(harness, content, themes.Styles);
    }

    private sealed record Shown(GuiTestHarness Harness, DiffContentView Content, IReadable<ThemeStyles> Styles)
        : IDisposable
    {
        public void Dispose() => Harness.Dispose();
    }
}

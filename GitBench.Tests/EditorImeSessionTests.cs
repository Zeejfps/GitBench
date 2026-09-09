using GitBench.Features.Diff;
using GitBench.Features.Editor;
using ZGF.Desktop.Input;
using ZGF.Geometry;
using ZGF.Gui.Desktop.Input;
using Xunit;

namespace GitBench.Tests;

/// <summary>The IME state machine: "the OS may compose" and "there is a caret to compose into" never drift apart.</summary>
public class EditorImeSessionTests
{
    private sealed class FakeImeHost : IImeHost
    {
        public bool Enabled;
        public int Resets;
        public RectF? CaretRect;

        public void SetImeEnabled(bool enabled) => Enabled = enabled;
        public void SetImeCaretRect(RectF caretRect) => CaretRect = caretRect;
        public void ResetComposition() => Resets++;
    }

    private static (ImeSession Session, FakeImeHost Host) Session()
    {
        var input = new InputSystem();
        var host = new FakeImeHost();
        input.ImeHost = host;
        return (new ImeSession(input), host);
    }

    private static ImeCaret At(int row, int column, float x = 10f) =>
        new ImeCaret.At(
            new DiffTextPos(new RowIndex(row), new ExpandedColumn(column)),
            new RectF { Left = x, Bottom = 0f, Width = 2f, Height = 16f });

    private static PreeditText Preedit(string text) =>
        new(text, text.Length, [new PreeditBlock(0, text.Length)], 0);

    [Fact]
    public void TheImeIsOffUntilThereIsACaretAndOffAgainAsSoonAsThereIsNot()
    {
        var (session, host) = Session();
        Assert.False(host.Enabled);

        session.Sync(At(0, 0));
        Assert.True(host.Enabled);

        session.Sync(ImeCaret.Nowhere);
        Assert.False(host.Enabled);
    }

    [Fact]
    public void LosingTheCaretDiscardsTheCompositionBeforeSwitchingTheImeOff()
    {
        var (session, host) = Session();
        session.Update(At(0, 0), Preedit("ni"));
        Assert.True(session.IsComposing);

        session.Sync(ImeCaret.Nowhere);

        Assert.Equal(1, host.Resets);
        Assert.False(host.Enabled);
        Assert.Null(session.Composition);
    }

    [Fact]
    public void ACompositionCannotSurviveTheCaretMovingOutFromUnderIt()
    {
        var (session, host) = Session();
        session.Update(At(4, 2), Preedit("ni"));

        session.Sync(At(2, 2));

        Assert.Equal(1, host.Resets);
        Assert.Null(session.Composition);
        Assert.True(host.Enabled);
    }

    [Fact]
    public void ACompositionSurvivesASyncThatMovedNothing()
    {
        var (session, _) = Session();
        session.Update(At(4, 2), Preedit("ni"));

        session.Sync(At(4, 2));

        Assert.Equal("ni", session.Composition!.Value.Preedit.Text);
    }

    [Fact]
    public void ACompositionOfferedWithNoCaretIsDropped()
    {
        var (session, host) = Session();

        session.Update(ImeCaret.Nowhere, Preedit("ni"));

        Assert.Null(session.Composition);
        Assert.False(host.Enabled);
    }

    [Fact]
    public void AnEmptyPreeditEndsTheCompositionWithoutTellingTheImeAnything()
    {
        var (session, host) = Session();
        session.Update(At(0, 0), Preedit("ni"));

        session.Update(At(0, 0), PreeditText.Empty);

        Assert.Null(session.Composition);
        Assert.Equal(0, host.Resets);
        Assert.True(host.Enabled);
    }

    [Fact]
    public void TheCandidateWindowIsMovedOnlyWhenTheCaretActuallyMoved()
    {
        var (session, host) = Session();

        session.Sync(At(0, 0, x: 10f));
        session.Sync(At(0, 0, x: 10f));
        Assert.Equal(10f, host.CaretRect!.Value.Left);

        session.Sync(At(0, 1, x: 18f));
        Assert.Equal(18f, host.CaretRect!.Value.Left);
    }

    [Fact]
    public void ACaretScrolledOutOfViewIsStillACaret()
    {
        var (session, host) = Session();
        session.Update(At(0, 0), Preedit("ni"));

        session.Sync(new ImeCaret.At(new DiffTextPos(new RowIndex(0), new ExpandedColumn(0)), null));

        Assert.True(host.Enabled);
        Assert.NotNull(session.Composition);
    }
}

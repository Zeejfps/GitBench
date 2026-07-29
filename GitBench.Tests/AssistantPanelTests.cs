using GitBench.App;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

// What Phase 7 gave the overlay: grips that resize it, a header that moves it, transcript text that
// can be selected, runs of tool calls folded into one line, and a way to start the conversation over.
public sealed class AssistantPanelTests
{
    // Away from the resting corner, so every edge has room to be dragged both ways.
    private static Preferences Placed(float x = 200f, float y = 100f) =>
        new() { AssistantPanelX = x, AssistantPanelY = y };

    private static AssistantViewFixture OpenPanel(
        FakeAssistantBackend? backend = null, Preferences? preferences = null)
    {
        var fixture = new AssistantViewFixture(
            backend ?? new FakeAssistantBackend(), preferences: preferences ?? Placed());
        fixture.PressPrimary(KeyboardKey.K);
        fixture.Frames();
        return fixture;
    }

    private static PointF GripCenter(AssistantViewFixture fixture, AssistantGrip grip) =>
        fixture.Harness.Root.FindById(AssistantResizeGrip.IdFor(grip))!.Position.Center;

    private static void DragGrip(AssistantViewFixture fixture, AssistantGrip grip, float dx, float dy) =>
        fixture.DragFrom(GripCenter(fixture, grip), dx, dy);

    [Fact]
    public void ASideGripResizesOnlyItsOwnAxis()
    {
        using var fixture = OpenPanel();
        var start = fixture.Placement.Rect.Value;

        DragGrip(fixture, AssistantGrip.Trailing, dx: 40f, dy: 0f);
        var wider = fixture.Placement.Rect.Value;
        Assert.Equal(start.Width + 40f, wider.Width, 1);
        Assert.Equal(start.Height, wider.Height, 1);
        Assert.Equal(start.X, wider.X, 1);

        // Window coordinates run up the screen, so dragging the bottom edge down is negative.
        DragGrip(fixture, AssistantGrip.Bottom, dx: 0f, dy: -30f);
        var taller = fixture.Placement.Rect.Value;
        Assert.Equal(wider.Width, taller.Width, 1);
        Assert.Equal(wider.Height + 30f, taller.Height, 1);
        Assert.Equal(wider.Y, taller.Y, 1);
    }

    [Fact]
    public void ACornerGripResizesBothAxesAtOnce()
    {
        using var fixture = OpenPanel();
        var start = fixture.Placement.Rect.Value;

        DragGrip(fixture, AssistantGrip.BottomTrailing, dx: 25f, dy: -35f);

        var resized = fixture.Placement.Rect.Value;
        Assert.Equal(start.Width + 25f, resized.Width, 1);
        Assert.Equal(start.Height + 35f, resized.Height, 1);
    }

    // A leading drag moves the edge it grabbed and leaves the other one where it was, so the panel
    // grows away from the pointer rather than sliding.
    [Fact]
    public void ALeadingGripHoldsTheTrailingEdgeStill()
    {
        using var fixture = OpenPanel();
        var start = fixture.Placement.Rect.Value;

        DragGrip(fixture, AssistantGrip.Leading, dx: -50f, dy: 0f);

        var resized = fixture.Placement.Rect.Value;
        Assert.Equal(start.X - 50f, resized.X, 1);
        Assert.Equal(start.X + start.Width, resized.X + resized.Width, 1);
    }

    [Fact]
    public void ResizingStopsAtTheSmallestUsableSize()
    {
        using var fixture = OpenPanel();

        DragGrip(fixture, AssistantGrip.Leading, dx: 4000f, dy: 0f);
        Assert.Equal(AssistantPanelPlacement.MinWidth, fixture.Placement.Rect.Value.Width, 1);

        DragGrip(fixture, AssistantGrip.Top, dx: 0f, dy: -4000f);
        Assert.Equal(AssistantPanelPlacement.MinHeight, fixture.Placement.Rect.Value.Height, 1);

        // Still a panel rather than a sliver: the composer and some transcript are both on screen.
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantComposer.InputId));
        Assert.True(fixture.Pane().Position.Height > 60f);
    }

    [Fact]
    public void ResizingStopsAtTheWindowItFloatsIn()
    {
        using var fixture = OpenPanel();

        DragGrip(fixture, AssistantGrip.Leading, dx: -4000f, dy: 0f);
        DragGrip(fixture, AssistantGrip.Bottom, dx: 0f, dy: 4000f);

        var rect = fixture.Placement.Rect.Value;
        Assert.True(rect.X >= 0f, $"the panel ran off the leading edge to {rect.X}");
        Assert.True(rect.X + rect.Width <= AssistantViewFixture.WindowWidth,
            $"the panel ran off the trailing edge to {rect.X + rect.Width}");
        Assert.True(rect.Y >= 0f, $"the panel ran off the top to {rect.Y}");
    }

    // One fact rather than a theory: the grips are an internal enum, which an xUnit InlineData
    // parameter cannot name.
    [Fact]
    public void TheCursorSaysWhichZoneThePointerIsIn()
    {
        (AssistantGrip Grip, MouseCursor Cursor)[] zones =
        [
            (AssistantGrip.Leading, MouseCursor.ResizeHorizontal),
            (AssistantGrip.Trailing, MouseCursor.ResizeHorizontal),
            (AssistantGrip.Top, MouseCursor.ResizeVertical),
            (AssistantGrip.Bottom, MouseCursor.ResizeVertical),
            (AssistantGrip.TopLeading, MouseCursor.ResizeNwse),
            (AssistantGrip.BottomTrailing, MouseCursor.ResizeNwse),
            (AssistantGrip.TopTrailing, MouseCursor.ResizeNesw),
            (AssistantGrip.BottomLeading, MouseCursor.ResizeNesw),
        ];

        using var fixture = OpenPanel();

        foreach (var (grip, cursor) in zones)
        {
            var center = GripCenter(fixture, grip);
            fixture.HoverAt(center.X, center.Y);
            Assert.True(cursor == fixture.Harness.Input.DesiredCursor,
                $"{grip} asked for {fixture.Harness.Input.DesiredCursor}, expected {cursor}");
        }
    }

    // Everything the placement holds is inline rather than left/right, so mirroring the layout
    // mirrors the panel, the zones around it and the direction a drag pulls in.
    [Fact]
    public void MirroringTheLayoutMirrorsThePanelAndItsZones()
    {
        using var fixture = OpenPanel();
        fixture.Harness.Root.IsRtl = true;
        fixture.Frames();

        var stored = fixture.Placement.Rect.Value;
        var panel = fixture.Harness.Root.FindById(AssistantOverlay.PanelId)!.Position;
        Assert.Equal(AssistantViewFixture.WindowWidth - stored.X - stored.Width, panel.Left, 1);

        // The leading zone is now the right-hand one, and the corners take the other diagonal.
        var leading = GripCenter(fixture, AssistantGrip.Leading);
        Assert.True(leading.X > panel.Center.X, "the leading zone did not move to the trailing side");
        fixture.HoverAt(leading.X, leading.Y);
        Assert.Equal(MouseCursor.ResizeHorizontal, fixture.Harness.Input.DesiredCursor);

        var corner = GripCenter(fixture, AssistantGrip.TopLeading);
        fixture.HoverAt(corner.X, corner.Y);
        Assert.Equal(MouseCursor.ResizeNesw, fixture.Harness.Input.DesiredCursor);

        // Pulling the leading edge outward means pulling it right here, and it still widens.
        DragGrip(fixture, AssistantGrip.Leading, dx: 40f, dy: 0f);
        Assert.Equal(stored.Width + 40f, fixture.Placement.Rect.Value.Width, 1);
    }

    [Fact]
    public void TheHeaderMovesThePanelAndTheTranscriptDoesNot()
    {
        using var fixture = OpenPanel();
        var start = fixture.Placement.Rect.Value;

        // Clear of the header's own buttons, which answer for their own clicks.
        var header = fixture.Harness.Root.FindById(AssistantPanel.HeaderId)!.Position;
        fixture.DragFrom(new PointF(header.Left + 60f, header.Center.Y), dx: -40f, dy: -30f);

        var moved = fixture.Placement.Rect.Value;
        Assert.Equal(start.X - 40f, moved.X, 1);
        Assert.Equal(start.Y + 30f, moved.Y, 1);
        Assert.Equal(start.Width, moved.Width, 1);
        Assert.Equal(start.Height, moved.Height, 1);

        fixture.DragFrom(fixture.Pane().Position.Center, dx: -40f, dy: -30f);

        Assert.Equal(moved, fixture.Placement.Rect.Value);
    }

    [Fact]
    public void MovingStopsAtTheWindowItFloatsIn()
    {
        using var fixture = OpenPanel();

        var header = fixture.Harness.Root.FindById(AssistantPanel.HeaderId)!.Position;
        fixture.DragFrom(new PointF(header.Left + 60f, header.Center.Y), dx: 4000f, dy: 4000f);

        var rect = fixture.Placement.Rect.Value;
        Assert.True(rect.X + rect.Width <= AssistantViewFixture.WindowWidth,
            $"the panel was dragged off the trailing edge to {rect.X + rect.Width}");
        Assert.True(rect.Y >= 0f, $"the panel was dragged above the window to {rect.Y}");
    }

    // Restore-then-shrink is the case that bites: a spot saved on a wide window is off-screen on a
    // narrow one, so the stored value is re-clamped rather than trusted.
    [Fact]
    public void AStoredSpotOutsideAShrunkenWindowIsPulledBackIn()
    {
        using var fixture = OpenPanel(preferences: Placed(x: 400f, y: 200f));
        Assert.Equal(400f, fixture.Placement.Rect.Value.X, 1);

        fixture.Harness.Resize(600, 500);
        fixture.Frames();

        var rect = fixture.Placement.Rect.Value;
        Assert.True(rect.X + rect.Width <= 600f, $"the panel stayed off the trailing edge at {rect.X}");
        Assert.True(rect.Y + rect.Height <= 500f, $"the panel stayed off the bottom at {rect.Y}");
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    // A reply is markdown, and is rendered as such: the model's headings and emphasis read as
    // headings and emphasis rather than as the characters they are spelled with.
    [Fact]
    public void AReplyRendersAsMarkdown()
    {
        using var fixture = OpenPanel(Answering("## Ahead\n\n**two** commits ahead of origin."));
        fixture.Ask("where am I");

        var canvas = fixture.Harness.Render();

        Assert.True(AssistantViewFixture.HasText(canvas, "Ahead"));
        Assert.True(AssistantViewFixture.HasText(canvas, "two"));
        Assert.False(AssistantViewFixture.HasText(canvas, "##"), "the heading marker was drawn as text");
        Assert.False(AssistantViewFixture.HasText(canvas, "**"), "the emphasis markers were drawn as text");
    }

    // A rendered document is not a field, so the drag that lifts part of a notice out of the app
    // cannot lift part of a reply. What replaces it takes the answer whole — as the markdown the
    // model wrote, which is what pastes usefully somewhere else.
    [Fact]
    public void AReplyIsCopiedWholeAndAsItsSource()
    {
        const string answer = "## Ahead\n\n**two** commits ahead of origin.";
        using var fixture = OpenPanel(Answering(answer));
        fixture.Ask("where am I");

        fixture.Harness.Click(fixture.Localization.Strings.Value.CommonCopy);

        Assert.Equal(answer, fixture.Clipboard.Text);
    }

    // The question the reader typed stays a read-only field: it is their own text, not markdown the
    // model wrote, and part of it must still be selectable.
    [Fact]
    public void TheQuestionStaysSelectableText()
    {
        using var fixture = OpenPanel(Answering("anything"));
        fixture.Ask("where am I");

        var body = fixture.Harness.Root.SelfAndDescendants()
            .OfType<TextInputView>()
            .First(v => v.Text.ToString() == "where am I");

        var rect = body.Position;
        fixture.DragFrom(new PointF(rect.Left + 4f, rect.Top - 4f), dx: 40f, dy: 0f);

        Assert.True(body.IsSelecting, "dragging across the question selected nothing");

        fixture.Harness.Type("nonsense");
        Assert.Equal("where am I", body.Text.ToString());
    }

    private static FakeAssistantBackend Answering(string reply) =>
        new(new BackendEvent[]
        {
            new BackendEvent.TextDelta(reply),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });

    private static FakeAssistantBackend ToolRun(params string[] tools)
    {
        var calls = new List<BackendEvent>();
        for (var i = 0; i < tools.Length; i++)
            calls.Add(new BackendEvent.ToolUse($"call_{i}", tools[i], AssistantTestJson.Empty));
        calls.Add(new BackendEvent.TurnComplete(StopReason.ToolUse));

        return new FakeAssistantBackend(
            calls,
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("Done."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });
    }

    private static int CountText(RecordingCanvas canvas, string text)
    {
        var found = 0;
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text.Contains(text, StringComparison.Ordinal)) found++;
        return found;
    }

    [Fact]
    public void ARunOfToolCallsIsOneRowThatOpensToAllOfThem()
    {
        using var fixture = OpenPanel(ToolRun("get_status", "get_status", "get_status"));
        fixture.Ask("look around");

        var session = fixture.Vm.Session.Value!;
        var tools = session.Rows.Where(r => r.Kind == AssistantRowKind.Tool).ToArray();
        Assert.Equal(3, Assert.Single(tools).Group!.Calls.Count);

        var collapsed = fixture.Harness.Render();
        Assert.True(AssistantViewFixture.HasText(collapsed, "Used 3 tools"));
        Assert.Equal(0, CountText(collapsed, "Used get_status"));

        fixture.Harness.ClickOn(ToolGroupSummary.ToggleId);
        fixture.Frames();

        Assert.Equal(3, CountText(fixture.Harness.Render(), "Used get_status"));
    }

    [Fact]
    public void AMessageBetweenTwoRunsYieldsTwoGroups()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "get_status", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("Checking the other side."),
                new BackendEvent.ToolUse("call_2", "get_status", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("Done."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });
        using var fixture = OpenPanel(backend);
        fixture.Ask("look around");

        var session = fixture.Vm.Session.Value!;
        var groups = session.Rows
            .Where(r => r.Kind == AssistantRowKind.Tool)
            .Select(r => r.Group!)
            .ToArray();

        Assert.Equal(2, groups.Length);
        Assert.NotSame(groups[0], groups[1]);
        Assert.All(groups, g => Assert.Single(g.Calls));
    }

    // A group that looks calm over a failure is worse than the lines it replaced.
    [Fact]
    public void AFailedCallIsSignalledWhileTheRunIsStillFolded()
    {
        using var fixture = OpenPanel(ToolRun("get_status", "no_such_tool", "get_status"));
        fixture.Ask("look around");

        var group = fixture.Vm.Session.Value!.Rows
            .Single(r => r.Kind == AssistantRowKind.Tool).Group!;
        Assert.False(group.IsExpanded.Value);
        Assert.Equal(1, group.FailedCount.Value);

        var canvas = fixture.Harness.Render();
        Assert.True(AssistantViewFixture.HasText(canvas, "1 failed"));
        Assert.True(AssistantViewFixture.HasText(canvas, GitBench.Controls.LucideIcons.TriangleAlert));
    }

    // The fold belongs to the conversation, not to the row that happens to be drawing it — the
    // overlay is non-modal and gets closed and reopened mid-thought.
    [Fact]
    public void AnOpenedRunStaysOpenAcrossClosingAndReopeningTheOverlay()
    {
        using var fixture = OpenPanel(ToolRun("get_status", "get_status", "get_status"));
        fixture.Ask("look around");

        fixture.Harness.ClickOn(ToolGroupSummary.ToggleId);
        fixture.Frames();
        Assert.Equal(3, CountText(fixture.Harness.Render(), "Used get_status"));

        fixture.Press(KeyboardKey.Escape);
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Frames();

        Assert.Equal(3, CountText(fixture.Harness.Render(), "Used get_status"));
    }

    [Fact]
    public void ClearingEmptiesThisRepositorysThreadAndLeavesTheOthers()
    {
        using var fixture = OpenPanel();
        fixture.Ask("first");
        var here = fixture.Vm.Session.Value!;
        Assert.NotEmpty(here.Rows);

        fixture.OpenRepo("other");
        var elsewhere = fixture.Vm.Session.Value!;
        Assert.NotSame(here, elsewhere);
        fixture.Ask("second");

        fixture.Registry.SetActive(here.RepoId);
        fixture.Vm.ClearConversation.Execute();
        fixture.Frames();

        Assert.Empty(here.Rows);
        Assert.NotEmpty(elsewhere.Rows);
        // Clearing a conversation is not signing out.
        Assert.False(fixture.Vm.NeedsSetup.Value);
    }

    [Fact]
    public void ClearingIsUndoneFromTheFeedbackItRaises()
    {
        using var fixture = OpenPanel();
        fixture.Ask("first");
        var session = fixture.Vm.Session.Value!;
        var before = session.Rows.ToArray();

        GitBench.Features.Notifications.ToastIntent? raised = null;
        fixture.Bus.Subscribe<GitBench.Messages.ShowToastMessage>(m => raised = m.Intent);

        fixture.Vm.ClearConversation.Execute();
        fixture.Frames();
        Assert.Empty(session.Rows);

        var action = raised?.Action;
        Assert.NotNull(action);
        action.Invoke();
        fixture.Frames();

        Assert.Equal(before, session.Rows.ToArray());
    }

    private static FakeAssistantBackend Staging(string path) =>
        new(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse(
                    "call_1", "stage_files", AssistantTestJson.Element($$"""{"paths":["{{path}}"]}""")),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("Staged it."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

    // Clearing mid-turn must not leave a stream writing into a transcript that is no longer there,
    // and the question the turn was suspended on goes with the rows rather than outliving them.
    [Fact]
    public void ClearingMidTurnStopsTheTurnFirstAndTakesThePendingQuestionWithIt()
    {
        using var fixture = OpenPanel(Staging("a.txt"));
        fixture.AskWithoutWaiting("stage a.txt");
        fixture.WaitForApproval();

        var session = fixture.Vm.Session.Value!;
        var pending = session.Rows.Single(r => r.Kind == AssistantRowKind.Approval).Pending!;

        fixture.Vm.ClearConversation.Execute();
        Assert.True(fixture.Vm.IsBusy.Value, "the rows went before the turn writing into them was down");
        Assert.NotEmpty(session.Rows);

        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the cleared turn to unwind");
        fixture.Frames();

        Assert.Empty(session.Rows);
        Assert.NotEqual(ToolApprovalOutcome.Pending, pending.Outcome.Value);
    }

    [Fact]
    public void ClearingIsOfferedOnlyWhileThereIsAThreadToDiscard()
    {
        using var fixture = OpenPanel();
        Assert.False(fixture.Vm.ClearConversation.CanExecute.Value);

        fixture.Ask("something");
        Assert.True(fixture.Vm.ClearConversation.CanExecute.Value);

        fixture.Vm.ClearConversation.Execute();
        fixture.Frames();
        Assert.False(fixture.Vm.ClearConversation.CanExecute.Value);
    }
}

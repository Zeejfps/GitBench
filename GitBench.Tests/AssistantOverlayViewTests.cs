using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Geometry;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

// The assistant's surfaces driven headlessly against a scripted backend: the overlay toggles on the
// keybind, streamed output and tool calls land in the transcript, a failed turn stays inside it
// rather than becoming a dialog, and the launcher is absent when there is no repo to reason about.
public sealed class AssistantOverlayViewTests
{
    [Fact]
    public void KeybindOpensAndClosesTheOverlay()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());

        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.PressPrimary(KeyboardKey.K);
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.PressPrimary(KeyboardKey.K);
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    [Fact]
    public void EscapeClosesTheOpenOverlay()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());

        fixture.PressPrimary(KeyboardKey.K);
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.Press(KeyboardKey.Escape);
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    // The connection card takes the composer's place, and which fields it offers follows the
    // provider: a hosted one is signed and lives at a fixed address, a local one is the user's to
    // point at — and takes a key too, because a gateway in front of it may ask for one.
    [Fact]
    public void TheSettingsGearOffersTheFieldsTheChosenProviderTakes()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());

        fixture.PressPrimary(KeyboardKey.K);
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantComposer.InputId));

        fixture.Harness.ClickOn(AssistantPanel.SettingsId);
        fixture.Harness.Layout();

        Assert.Null(fixture.Harness.Root.FindById(AssistantComposer.InputId));
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantSettingsCard.ProviderId));
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantSettingsCard.ModelInputId));
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantSettingsCard.KeyInputId));
        Assert.Null(fixture.Harness.Root.FindById(AssistantSettingsCard.BaseUrlInputId));

        fixture.Vm.SetProviderDraft(AssistantProviders.Ollama.Id);
        fixture.Harness.Layout();

        Assert.NotNull(fixture.Harness.Root.FindById(AssistantSettingsCard.BaseUrlInputId));
        // The endpoint is not the only box a token could be smuggled into.
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantSettingsCard.KeyInputId));
        Assert.True(fixture.Vm.IsApiKeyOptional.Value);

        fixture.Harness.ClickOn(AssistantSettingsCard.CancelId);
        fixture.Harness.Layout();
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantComposer.InputId));
    }

    [Fact]
    public void StreamedTextLandsInTheTranscript()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TextDelta("main is "),
            new BackendEvent.TextDelta("two ahead."),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });
        using var fixture = new AssistantViewFixture(backend);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Ask("where am I");

        var canvas = fixture.Harness.Render();
        // The deltas are appended to one row, so the transcript shows the joined answer.
        Assert.True(AssistantViewFixture.HasText(canvas, "main is two ahead."));
        Assert.True(AssistantViewFixture.HasText(canvas, "where am I"));
    }

    [Fact]
    public void ToolCallRendersAsOneCollapsedRow()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "get_status", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("Clean."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });
        using var fixture = new AssistantViewFixture(backend);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Ask("status?");

        var canvas = fixture.Harness.Render();
        Assert.True(AssistantViewFixture.HasText(canvas, "Used get_status"));
        Assert.True(AssistantViewFixture.HasText(canvas, "Clean."));
        // The row names the tool and stops there — no arguments, no result payload.
        foreach (var text in canvas.Texts)
            Assert.DoesNotContain("\"branch\"", text.Inputs.Text);
    }

    [Fact]
    public void FailedTurnRendersInlineRatherThanAsADialog()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.Error("overloaded", "overloaded_error"),
        });
        using var fixture = new AssistantViewFixture(backend);

        var dialogs = 0;
        fixture.Bus.Subscribe<ShowOperationErrorMessage>(_ => dialogs++);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Ask("anything");

        var canvas = fixture.Harness.Render();
        Assert.True(AssistantViewFixture.HasText(canvas, "overloaded"));
        Assert.Equal(0, dialogs);
        // Still usable afterwards: a failed turn is a message, not a stuck state.
        Assert.False(fixture.Vm.IsBusy.Value);
    }

    [Fact]
    public void TranscriptFollowsNewContentWhileRestingAtTheBottom()
    {
        using var fixture = new AssistantViewFixture(Chatty(6));
        fixture.PressPrimary(KeyboardKey.K);

        for (var i = 0; i < 6; i++) fixture.Ask($"question {i}");

        var pane = fixture.Pane();
        Assert.True(pane.Scale < 1f, $"the transcript needs to overflow for this to mean anything (scale {pane.Scale})");
        Assert.True(pane.ScrollNormalized > 0.99f, $"expected to be pinned to the bottom, was {pane.ScrollNormalized}");
    }

    [Fact]
    public void TranscriptStaysPutAfterTheReaderScrollsUp()
    {
        using var fixture = new AssistantViewFixture(Chatty(8));
        fixture.PressPrimary(KeyboardKey.K);

        for (var i = 0; i < 5; i++) fixture.Ask($"question {i}");
        Assert.True(fixture.Pane().ScrollNormalized > 0.99f);

        // A deliberate wheel scroll away from the end releases the pin.
        fixture.ScrollTranscriptUp();
        var parked = fixture.Pane().ScrollNormalized;
        Assert.True(parked < 0.9f, $"the wheel should have moved off the bottom, was {parked}");

        for (var i = 0; i < 3; i++) fixture.Ask($"later {i}");

        // Content grew below, so the same offset is a smaller fraction of a longer transcript; what
        // must not happen is being yanked back to the end.
        var after = fixture.Pane().ScrollNormalized;
        Assert.True(after < 0.99f, $"expected to stay parked, was pulled to {after}");
    }

    [Fact]
    public void TranscriptResumesFollowingAfterScrollingBackToTheBottom()
    {
        using var fixture = new AssistantViewFixture(Chatty(8));
        fixture.PressPrimary(KeyboardKey.K);

        for (var i = 0; i < 5; i++) fixture.Ask($"question {i}");
        fixture.ScrollTranscriptUp();
        Assert.True(fixture.Pane().ScrollNormalized < 0.9f);

        fixture.ScrollTranscriptToBottom();
        Assert.True(fixture.Pane().ScrollNormalized > 0.99f);

        fixture.Ask("one more");

        Assert.True(fixture.Pane().ScrollNormalized > 0.99f, "the pin should have been taken again");
    }

    [Fact]
    public void ReplyLanguageFollowsTheAppsLocale()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });
        using var fixture = new AssistantViewFixture(backend, locale: Locale.Ja);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Ask("what changed");

        var context = Assert.Single(Assert.Single(backend.Requests).Messages.OfType<AssistantMessage.RepoContext>());
        Assert.Contains("Reply in Japanese.", context.Text);
    }

    // The system block carries cache_control: ephemeral, so it is the cached prefix. If a user
    // setting ever gets templated into it, every language change re-bills the whole prefix — the
    // language instruction has to stay in the uncached per-turn block.
    [Fact]
    public void LocaleDoesNotChangeTheCachedSystemPrefix()
    {
        static string SystemPromptFor(Locale locale)
        {
            var backend = new FakeAssistantBackend(new BackendEvent[]
            {
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });
            using var fixture = new AssistantViewFixture(backend, locale: locale);
            fixture.PressPrimary(KeyboardKey.K);
            fixture.Ask("hello");
            return Assert.Single(backend.Requests).SystemPrompt;
        }

        var english = SystemPromptFor(Locale.En);
        Assert.Equal(english, SystemPromptFor(Locale.Ja));
        Assert.Equal(english, SystemPromptFor(Locale.Ar));
    }

    // Pseudo is the generator's synthetic locale, built on the reference catalog — it must read as
    // English rather than asking the model to answer in mangled text.
    [Fact]
    public void PseudoLocaleAsksForEnglish()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });
        using var fixture = new AssistantViewFixture(backend, locale: Locale.Pseudo);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.Ask("hello");

        var context = Assert.Single(Assert.Single(backend.Requests).Messages.OfType<AssistantMessage.RepoContext>());
        Assert.Contains("Reply in English.", context.Text);
    }

    // The mark's art is far larger than the toolbar asks for, and an image is drawn into whatever
    // rect layout hands it — so the mark has to hold its own square, at the icon size, centered like
    // the Lucide glyphs beside it. Size alone is not enough of an assertion: it was the right size
    // and sitting low at the same time.
    [Fact]
    public void ToolbarMarkDrawsAtIconSizeCenteredInItsButton()
    {
        const string markId = "assistant-mark";
        using var fixture = WithMark(markId, out var restore);
        using (restore)
        {
            var drawn = Assert.Single(fixture.Harness.Render().Images);
            Assert.Equal(markId, drawn.Inputs.ImageId);
            Assert.Equal(16f, drawn.Inputs.Position.Width, 1);
            Assert.Equal(16f, drawn.Inputs.Position.Height, 1);
            AssertCenteredInButton(fixture, drawn.Inputs.Position);
        }
    }

    // The image path is what ships, so the glyph path is the one that rots unnoticed — it only
    // appears when the asset fails to load.
    [Fact]
    public void ToolbarMarkFallbackGlyphIsCenteredTheSameWay()
    {
        var previous = AssistantMark.ImageId.Value;
        AssistantMark.ImageId.Value = null;
        try
        {
            using var fixture = new AssistantViewFixture(new FakeAssistantBackend());

            var canvas = fixture.Harness.Render();
            var glyph = canvas.Texts.Single(t => t.Inputs.Text == LucideIcons.SquareTerminal);
            Assert.Empty(canvas.Images);
            AssertCenteredInButton(fixture, glyph.Inputs.Position);
        }
        finally
        {
            AssistantMark.ImageId.Value = previous;
        }
    }

    private static AssistantViewFixture WithMark(string markId, out IDisposable restore)
    {
        var previous = AssistantMark.ImageId.Value;
        restore = new Restore(() => AssistantMark.ImageId.Value = previous);
        AssistantMark.ImageId.Value = markId;
        // The real mark is 96px art asked for at 16.
        return new AssistantViewFixture(new FakeAssistantBackend(), configureCanvas: c => c.SetImageSize(markId, 96, 96));
    }

    private static void AssertCenteredInButton(AssistantViewFixture fixture, RectF mark)
    {
        var button = fixture.Harness.Root.FindById(AssistantToolbarButton.ButtonId)!.Position;
        Assert.True(mark.Width <= button.Width && mark.Height <= button.Height,
            $"the mark ({mark.Width}x{mark.Height}) spilled out of its button ({button.Width}x{button.Height})");
        Assert.Equal(button.Center.X, mark.Center.X, 1);
        Assert.Equal(button.Center.Y, mark.Center.Y, 1);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    // A backend that answers every turn with enough lines to overflow the panel several times over.
    // The answers carry explicit newlines rather than relying on wrapping, so the height they take is
    // the same under the harness's synthetic text measurer as it is on screen.
    private static FakeAssistantBackend Chatty(int turns)
    {
        var scripts = new IReadOnlyList<BackendEvent>[turns];
        for (var i = 0; i < turns; i++)
        {
            var body = string.Join("\n", Enumerable.Range(0, 6).Select(n => $"detail line {n} about the tree"));
            scripts[i] =
            [
                new BackendEvent.TextDelta($"Answer {i}:\n"),
                new BackendEvent.TextDelta(body),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            ];
        }
        return new FakeAssistantBackend(scripts);
    }

    [Fact]
    public void ShiftEnterInsertsANewlineInsteadOfSending()
    {
        var backend = new FakeAssistantBackend();
        using var fixture = new AssistantViewFixture(backend);
        fixture.PressPrimary(KeyboardKey.K);
        fixture.FocusComposer();

        fixture.Harness.Type("first");
        fixture.Press(KeyboardKey.Enter, InputModifiers.Shift);
        fixture.Harness.Type("second");

        Assert.Equal("first\nsecond", fixture.Field().TextValue.Value);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public void PlainEnterSendsTheDraft()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TextDelta("on it"),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });
        using var fixture = new AssistantViewFixture(backend);
        fixture.PressPrimary(KeyboardKey.K);
        fixture.FocusComposer();

        fixture.Harness.Type("what changed");
        fixture.Press(KeyboardKey.Enter);
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the assistant turn to finish");
        fixture.Frames();

        Assert.Single(backend.Requests);
        // The field empties as the message becomes a transcript row.
        Assert.Equal(string.Empty, fixture.Field().TextValue.Value);
        Assert.True(AssistantViewFixture.HasText(fixture.Harness.Render(), "on it"));
    }

    [Fact]
    public void ComposerGrowsWithLinesAndStopsAtItsCap()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());
        fixture.PressPrimary(KeyboardKey.K);
        fixture.FocusComposer();

        var oneLine = fixture.Field().Position.Height;

        fixture.Harness.Type("one");
        fixture.Press(KeyboardKey.Enter, InputModifiers.Shift);
        fixture.Harness.Type("two");
        var twoLines = fixture.Field().Position.Height;
        Assert.True(twoLines > oneLine, $"expected growth past {oneLine}, got {twoLines}");

        for (var i = 0; i < 20; i++)
        {
            fixture.Press(KeyboardKey.Enter, InputModifiers.Shift);
            fixture.Harness.Type($"line {i}");
        }
        var capped = fixture.Field().Position.Height;
        Assert.Equal(AssistantComposer.FieldMaxHeight, capped, 1);

        // Asserting against the constant alone would follow it wherever it moves, so pin the floor
        // it exists to hold too: five lines of composer at the harness's 16px line plus chrome.
        Assert.True(capped >= 90f, $"the cap should leave room for about five lines, stopped at {capped}");

        // The transcript keeps a usable share of the fixed-height panel rather than being squeezed out.
        Assert.True(fixture.Pane().Position.Height > 100f);
    }

    [Fact]
    public void ToolbarButtonTogglesTheOverlay()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());

        fixture.ClickToolbarButton();
        Assert.NotNull(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.ClickToolbarButton();
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    // The toolbar disables repo-dependent buttons rather than removing them, so the row keeps its
    // shape; the assistant's entry point follows that and stays put but inert.
    [Fact]
    public void ToolbarButtonIsDisabledWithNoActiveRepo()
    {
        using var withRepo = new AssistantViewFixture(new FakeAssistantBackend());
        Assert.True(withRepo.Vm.IsAvailable.Value);
        Assert.True(withRepo.Vm.Toggle.CanExecute.Value);

        using var empty = new AssistantViewFixture(new FakeAssistantBackend(), openRepo: false);
        Assert.False(empty.Vm.IsAvailable.Value);
        Assert.False(empty.Vm.Toggle.CanExecute.Value);
        Assert.NotNull(empty.Harness.Root.FindById(AssistantToolbarButton.ButtonId));

        empty.ClickToolbarButton();
        Assert.Null(empty.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    [Fact]
    public void KeybindDoesNothingWithNoActiveRepo()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend(), openRepo: false);

        fixture.PressPrimary(KeyboardKey.K);

        Assert.False(fixture.Vm.IsOpen.Value);
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));
    }

    // A turn that asks to stage one file, then answers once it has been let through.
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

    [Fact]
    public void WriteToolWaitsOnACardShowingItsExactArguments()
    {
        var backend = Staging("a.txt");
        using var fixture = new AssistantViewFixture(backend);
        File.WriteAllText(Path.Combine(fixture.RepoPath, "a.txt"), "one\n");

        fixture.PressPrimary(KeyboardKey.K);
        fixture.AskWithoutWaiting("stage a.txt");
        fixture.WaitForApproval();

        var canvas = fixture.Harness.Render();
        Assert.True(AssistantViewFixture.HasText(canvas, "Run stage_files?"));
        Assert.True(AssistantViewFixture.HasText(canvas, """paths: ["a.txt"]"""));
        // Suspended, not finished — and nothing has run.
        Assert.True(fixture.Vm.IsBusy.Value);
        Assert.Single(backend.Requests);

        fixture.Harness.ClickOn(ToolApprovalActions.ApproveId);
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the approved turn to finish");
        fixture.Harness.Layout();

        var result = Assert.Single(
            backend.Requests[1].Messages.OfType<AssistantMessage.ToolResults>().Single().Results);
        Assert.False(result.IsError);
        Assert.True(AssistantViewFixture.HasText(fixture.Harness.Render(), "Approved"));
    }

    [Fact]
    public void DenyingAWriteSendsAnErrorResultAndLeavesTheSessionUsable()
    {
        var backend = Staging("a.txt");
        using var fixture = new AssistantViewFixture(backend);

        fixture.PressPrimary(KeyboardKey.K);
        fixture.AskWithoutWaiting("stage a.txt");
        fixture.WaitForApproval();

        fixture.Harness.ClickOn(ToolApprovalActions.DenyId);
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the declined turn to finish");
        fixture.Harness.Layout();

        var result = Assert.Single(
            backend.Requests[1].Messages.OfType<AssistantMessage.ToolResults>().Single().Results);
        Assert.True(result.IsError);
        Assert.True(AssistantViewFixture.HasText(fixture.Harness.Render(), "Denied"));
        Assert.False(fixture.Vm.IsBusy.Value);
    }

    // The overlay is non-modal, so it can be closed while a question is still waiting. The question
    // belongs to the session, not to the view — reopening finds it exactly where it was.
    [Fact]
    public void APendingApprovalSurvivesClosingAndReopeningTheOverlay()
    {
        using var fixture = new AssistantViewFixture(Staging("a.txt"));
        File.WriteAllText(Path.Combine(fixture.RepoPath, "a.txt"), "one\n");

        fixture.PressPrimary(KeyboardKey.K);
        fixture.AskWithoutWaiting("stage a.txt");
        fixture.WaitForApproval();

        fixture.Press(KeyboardKey.Escape);
        Assert.Null(fixture.Harness.Root.FindById(AssistantOverlay.PanelId));

        fixture.PressPrimary(KeyboardKey.K);
        Assert.True(AssistantViewFixture.HasText(fixture.Harness.Render(), "Run stage_files?"));

        fixture.Harness.ClickOn(ToolApprovalActions.ApproveId);
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the approved turn to finish");
    }

    [Fact]
    public void StoppingTheTurnWithdrawsThePendingApproval()
    {
        using var fixture = new AssistantViewFixture(Staging("a.txt"));

        fixture.PressPrimary(KeyboardKey.K);
        fixture.AskWithoutWaiting("stage a.txt");
        fixture.WaitForApproval();

        fixture.Vm.Stop.Execute();
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the stopped turn to unwind");
        fixture.Harness.Layout();

        // The card stops offering an answer to a turn that is no longer listening.
        Assert.True(AssistantViewFixture.HasText(fixture.Harness.Render(), "Stopped"));
        Assert.Null(fixture.Harness.Root.FindById(ToolApprovalActions.ApproveId));
    }

    // Non-modal means the app outside the panel stays live. It does not mean the panel is a hole:
    // a drag that starts on it must not reach the diff underneath.
    [Fact]
    public void PointerInputInsideTheOverlayDoesNotReachTheSurfaceBeneath()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());
        fixture.PressPrimary(KeyboardKey.K);

        var panel = fixture.Harness.Root.FindById(AssistantOverlay.PanelId)!.Position;
        // The header band, clear of the close button: panel surface with no interactive child of its
        // own, which is exactly where input used to fall straight through to the workspace.
        var close = fixture.Harness.Root.FindById(AssistantPanel.CloseId)!.Position;
        var inside = new PointF(panel.Left + 50f, close.Center.Y);
        Assert.True(panel.ContainsPoint(inside));
        Assert.False(close.ContainsPoint(inside));

        fixture.HoverAt(inside.X, inside.Y);
        fixture.Underneath.Reset();
        fixture.DragAt(inside.X, inside.Y);
        Assert.Equal(0, fixture.Underneath.Presses);
        Assert.Equal(0, fixture.Underneath.Moves);

        fixture.Underneath.Reset();
        fixture.WheelAt(inside.X, inside.Y);
        Assert.Equal(0, fixture.Underneath.Wheels);
    }

    [Fact]
    public void PointerInputOutsideTheOverlayStillReachesTheSurfaceBeneath()
    {
        using var fixture = new AssistantViewFixture(new FakeAssistantBackend());
        fixture.PressPrimary(KeyboardKey.K);

        var panel = fixture.Harness.Root.FindById(AssistantOverlay.PanelId)!.Position;
        // Bottom-leading, well clear of the top-trailing panel and of the toolbar row.
        var outside = new PointF(60f, 640f);
        Assert.False(panel.ContainsPoint(outside));


        fixture.HoverAt(outside.X, outside.Y);
        fixture.Underneath.Reset();
        fixture.DragAt(outside.X, outside.Y);
        Assert.Equal(1, fixture.Underneath.Presses);
        Assert.True(fixture.Underneath.Moves > 0, "the drag never reached the surface it started on");

        fixture.Underneath.Reset();
        fixture.WheelAt(outside.X, outside.Y);
        Assert.Equal(1, fixture.Underneath.Wheels);
    }
}

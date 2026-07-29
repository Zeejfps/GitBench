using GitBench.Features.Assistant;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// An error, a refusal or an advisory is the transcript text a reader most needs to lift out of the
// app — an endpoint's 400 body, a model id that did not exist. So a notice reads out of the same
// read-only field a reply does, rather than a label nothing can be dragged across.
public sealed class AssistantTranscriptNoticeTests
{
    private const string Failure = "the endpoint answered 400: the model \"gpt-9\" does not exist";
    private const string Explanation = "that would rewrite history other people have pulled";

    private static readonly InputModifiers Primary =
        OperatingSystem.IsMacOS() ? InputModifiers.Super : InputModifiers.Control;

    [Fact]
    public void AnErrorBodyIsSelectableAndNotEdited()
    {
        using var notice = Mount(AssistantRow.Error(Failure), TranscriptNoticeTone.Error);
        var body = notice.Body;
        Assert.True(body.ReadOnly, "the error body accepts edits");

        notice.DragAcross(80f);

        Assert.True(body.IsSelecting, "dragging across the error selected nothing");
        Assert.NotEqual(Failure, body.GetSelectedText());

        notice.Harness.Type("nonsense");
        Assert.Equal(Failure, body.Text.ToString());
    }

    [Fact]
    public void AnAdvisoryBodyIsSelectableToo()
    {
        using var notice = Mount(
            AssistantRow.Notice("the conversation was trimmed to fit the model's window"),
            TranscriptNoticeTone.Advisory);

        notice.DragAcross(80f);

        Assert.True(notice.Body.IsSelecting, "dragging across the advisory selected nothing");
    }

    [Fact]
    public void ASelectionInsideAnErrorRowReachesTheClipboard()
    {
        using var notice = Mount(AssistantRow.Error(Failure), TranscriptNoticeTone.Error);

        notice.DragAcross(80f);
        var selected = notice.Body.GetSelectedText();
        notice.Harness.PressKey(KeyboardKey.C, Primary);

        Assert.False(string.IsNullOrEmpty(selected), "the drag selected nothing to copy");
        Assert.Equal(selected, notice.Clipboard.Text);
        Assert.Contains(notice.Clipboard.Text!, Failure, StringComparison.Ordinal);
    }

    // The sentence around a decline is composed in the view, so it is the one part of a notice that
    // could end up as a label the reader cannot take with the rest.
    [Fact]
    public void TheRefusalPrefixIsPartOfTheSelectableText()
    {
        using var notice = Mount(AssistantRow.Refusal(Explanation), TranscriptNoticeTone.Refusal);
        var declined = notice.Localization.Strings.Value.AssistantRefused;

        notice.DragAcross(0f);
        notice.Harness.PressKey(KeyboardKey.A, Primary);
        notice.Harness.PressKey(KeyboardKey.C, Primary);

        Assert.Equal(declined + " " + Explanation, notice.Clipboard.Text);
    }

    private static MountedNotice Mount(AssistantRow row, TranscriptNoticeTone tone)
    {
        var clipboard = new FakeClipboard();
        var localization = new LocalizationService(new State<Locale>(Locale.En));
        var harness = GuiTestHarness.Create(
            ctx => new Column
            {
                MainAxis = MainAxisAlignment.Start,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [new TranscriptNoticeRow { Row = row, Tone = tone }],
            }.BuildView(ctx),
            width: 420,
            height: 200,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(localization);
                ctx.AddService<IClipboard>(clipboard);
            });
        harness.Layout();

        return new MountedNotice(harness, clipboard, localization, row);
    }

    private sealed class MountedNotice : IDisposable
    {
        private readonly AssistantRow _row;

        public MountedNotice(
            GuiTestHarness harness,
            FakeClipboard clipboard,
            ILocalizationService localization,
            AssistantRow row)
        {
            Harness = harness;
            Clipboard = clipboard;
            Localization = localization;
            _row = row;
        }

        public GuiTestHarness Harness { get; }
        public FakeClipboard Clipboard { get; }
        public ILocalizationService Localization { get; }

        public TextInputView Body =>
            Harness.Root.SelfAndDescendants().OfType<TextInputView>().Single();

        // Window coordinates run up the window, so a point just inside the top edge is below it.
        public void DragAcross(float dx)
        {
            var rect = Body.Position;
            var y = rect.Top - 4f;
            Harness.MoveTo(rect.Left + 4f, y);
            Harness.Press();
            Harness.MoveTo(rect.Left + 4f + dx, y);
            Harness.Release();
            Harness.Layout();
        }

        public void Dispose()
        {
            Harness.Dispose();
            _row.Dispose();
        }
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }
}

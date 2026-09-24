using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The review header's "Walk me through this": there while the assistant can answer, gone while it
/// cannot, and a click asks for a walkthrough of the window's repository.
/// </summary>
public sealed class WalkthroughStartButtonTests : IDisposable
{
    private const int Width = 800;
    private const int Height = 200;

    private sealed class FakeClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private readonly ReviewPresentationFixture _fixture = new();
    private readonly FakeAssistantSessionStore _store = new();
    private readonly List<NarrateWalkthroughMessage> _sent = new();
    private GuiTestHarness? _harness;

    public WalkthroughStartButtonTests() => _fixture.Bus.Subscribe<NarrateWalkthroughMessage>(_sent.Add);

    public void Dispose()
    {
        _harness?.Dispose();
        _store.Dispose();
        _fixture.Dispose();
    }

    private GuiTestHarness Mount(ReviewWindowViewModel window, bool withAssistant)
    {
        _harness = GuiTestHarness.Create(
            ctx => new Box
            {
                Width = Width,
                Height = Height,
                Children = [new Provide<ReviewWindowViewModel> { Value = window, Child = new ReviewHeaderBar() }],
            }.BuildView(ctx),
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService(_fixture.Localization);
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(_fixture.Dispatcher);
                if (withAssistant) ctx.AddService<IAssistantSessionStore>(_store);
            });
        _harness.Layout();
        return _harness;
    }

    [Fact]
    public void WhileTheAssistantIsConfigured_AClickAsksForAWalkthroughOfTheWindowsRepository()
    {
        _store.SetConfigured(true);
        var window = _fixture.OpenWindow();
        var harness = Mount(window, withAssistant: true);

        Assert.NotNull(harness.Root.FindById(WalkthroughStartButton.ButtonId));
        Assert.Contains(harness.Render().Texts, drawn => drawn.Inputs.Text.Contains("Walk me through this", StringComparison.Ordinal));

        harness.ClickOn(WalkthroughStartButton.ButtonId);

        var message = Assert.Single(_sent);
        Assert.Equal(window.Session.RepoId, message.RepoId);
        Assert.IsType<WalkthroughCue.Begin>(message.Cue);
    }

    // The rail is up from the click, and a second click while the first is still being answered
    // would only queue another walkthrough behind it.
    [Fact]
    public void AClick_PutsTheRailUp_AndTheButtonWaitsForTheFirstStep()
    {
        _store.SetConfigured(true);
        var window = _fixture.OpenWindow();
        var harness = Mount(window, withAssistant: true);

        harness.ClickOn(WalkthroughStartButton.ButtonId);
        harness.Layout();

        Assert.IsType<WalkthroughCard.Preparing>(window.Walkthrough.Card.Value);
        harness.ClickOn(WalkthroughStartButton.ButtonId);
        Assert.Single(_sent);

        window.Walkthrough.Show(Narrator.Assistant, [WalkthroughSteps.Step("one")]);
        harness.Layout();
        harness.ClickOn(WalkthroughStartButton.ButtonId);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void WhileNothingIsConfigured_TheButtonIsAbsent_AndAppearsOnceAKeyResolves()
    {
        var window = _fixture.OpenWindow();
        var harness = Mount(window, withAssistant: true);
        Assert.Null(harness.Root.FindById(WalkthroughStartButton.ButtonId));

        _store.SetConfigured(true);
        harness.Layout();

        Assert.NotNull(harness.Root.FindById(WalkthroughStartButton.ButtonId));
    }

    [Fact]
    public void WithNoAssistantInTheContext_TheButtonIsAbsent()
    {
        var window = _fixture.OpenWindow();
        var harness = Mount(window, withAssistant: false);

        Assert.Null(harness.Root.FindById(WalkthroughStartButton.ButtonId));
    }
}

using GitBench.Features.Pairing;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>The stop card's slot and the stop's controls mounted headlessly: the controls stay for
/// the whole session, and the card gives way to a placeholder while the agent works out the next
/// stop.</summary>
public sealed class PairingStopSlotTests : IDisposable
{
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

    private readonly RecordingPairingPresentation _presentation = new();
    private readonly ScriptedWorkspace _workspace = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly PairingStore _store;
    private readonly GuiTestHarness _harness;

    public PairingStopSlotTests()
    {
        _store = new PairingStore("Add a retry", "Claude Code", new AgentTranscript(), _presentation, _workspace, _dispatcher, new ManualTimeProvider());
        _store.MarkRunning();
        _harness = GuiTestHarness.Create(
            ctx => new Box
            {
                Width = 480,
                Height = 600,
                Children =
                [
                    new Column
                    {
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new PairingStopSlot { Store = _store },
                            new Show
                            {
                                When = new Derived<bool>(() => _store.IsLive),
                                Then = () => new PairingStopActions { Store = _store },
                            },
                        ],
                    },
                ],
            }.BuildView(ctx),
            width: 480,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(_dispatcher);
                ctx.AddService<IKeyMap>(new KeyMap());
                ctx.AddService<IMessageBus>(_bus);
            });
    }

    public void Dispose()
    {
        _harness.Dispose();
        _store.Dispose();
    }

    private void Open()
    {
        var opening = _store.OpenStopAsync(new StopTarget("src/Client.cs", "Fetch", null), "Retry Fetch", "Because.",
            new DraftRequest("public void Fetch() => Retry(Send);", new DraftSpan.Declaration()), false, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => opening.IsCompleted, "the stop to open");
        Assert.IsType<StopOpening.Opened>(opening.Result);
        _harness.Layout();
    }

    private bool Has(string id) => _harness.Root.FindById(id) is not null;

    [Fact]
    public void BeforeTheFirstStop_ThePlaceholderAndTheControlsAreUp()
    {
        _harness.Layout();

        Assert.True(Has(PairingStopPlaceholder.PlaceholderId));
        Assert.False(Has(PairingStopCard.CardId));
        Assert.True(Has(PairingStopActions.BarId));
        Assert.True(Has(PairingStopActions.AcceptAndNextId));
    }

    [Fact]
    public void AnOpenStop_TakesThePlaceholdersPlace()
    {
        Open();

        Assert.True(Has(PairingStopCard.CardId));
        Assert.False(Has(PairingStopPlaceholder.PlaceholderId));
        Assert.True(Has(PairingStopActions.BarId));
    }

    [Fact]
    public void AfterNext_TheControlsStay_WhileThePlaceholderHoldsTheCardsPlace()
    {
        Open();

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Next to finish");
        _harness.Layout();

        Assert.Null(_store.Stop.Value);
        Assert.True(Has(PairingStopActions.BarId));
        Assert.True(Has(PairingStopActions.NextId));
        Assert.True(Has(PairingStopPlaceholder.PlaceholderId));
        Assert.False(Has(PairingStopCard.CardId));
    }

    [Fact]
    public void WhenTheSessionEnds_TheCardAndTheControlsGo()
    {
        Open();

        _store.End("All done.");
        _harness.Layout();

        Assert.False(Has(PairingStopCard.CardId));
        Assert.False(Has(PairingStopPlaceholder.PlaceholderId));
        Assert.False(Has(PairingStopActions.BarId));
    }

    [Fact]
    public void End_AsksFirst_AndLeavesTheSessionRunning()
    {
        var asked = new List<ShowDialogMessage>();
        _bus.Subscribe<ShowDialogMessage>(asked.Add);
        Open();

        _harness.ClickOn(PairingStopActions.EndId);

        Assert.Single(asked);
        Assert.True(_store.IsLive);
    }
}

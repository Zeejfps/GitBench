using GitBench.Features.AgentConnections;
using GitBench.Features.Settings;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>The agent-connections settings rows and the status-bar badge mounted headlessly:
/// what they show for each server state, and what the toggle and the copy button do.</summary>
public sealed class AgentConnectionsSettingsTests
{
    private const int Width = 700;
    private const int Height = 400;

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private readonly State<AgentConnectionSettings> _settings = new(new AgentConnectionSettings(false, 5577, null));
    private readonly State<AgentConnectionState> _state = new(new AgentConnectionState.Off());
    private readonly FakeClipboard _clipboard = new();
    private readonly MessageBus _bus = new();

    private GuiTestHarness Mount(IWidget widget) => GuiTestHarness.Create(
        ctx => new Box { Width = Width, Height = Height, Children = [widget] }.BuildView(ctx),
        width: Width,
        height: Height,
        configure: ctx =>
        {
            ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
            ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            ctx.AddService(_settings);
            ctx.AddService(_state);
            ctx.AddService<IClipboard>(_clipboard);
            ctx.AddService<IMessageBus>(_bus);
        });

    private static bool HasText(RecordingCanvas canvas, string text)
    {
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text.Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    [Fact]
    public void Off_ShowsOff_AndThePort()
    {
        using var harness = Mount(new AgentConnectionsSettingsSection());

        var canvas = harness.Render();

        Assert.True(HasText(canvas, "Allow local agents to connect"));
        Assert.True(HasText(canvas, "Off"));
        Assert.True(HasText(canvas, "5577"));
    }

    [Fact]
    public void ClickingTheToggle_EnablesThePreference()
    {
        using var harness = Mount(new AgentConnectionsSettingsSection());

        harness.ClickOn(AgentConnectionsSettingsSection.EnabledId);
        harness.Layout();

        Assert.True(_settings.Value.Enabled);
    }

    [Fact]
    public void Listening_ShowsTheEndpoint_AndCopyWritesTheCommandAndToasts()
    {
        var endpoint = new Uri("http://127.0.0.1:5577/mcp/abc123");
        _state.Value = new AgentConnectionState.Listening(endpoint, 0);
        var toasts = new List<ShowToastMessage>();
        _bus.Subscribe<ShowToastMessage>(toasts.Add);
        using var harness = Mount(new AgentConnectionsSettingsSection());
        Assert.True(HasText(harness.Render(), "Listening on http://127.0.0.1:5577/mcp/abc123"));

        harness.ClickOn(AgentConnectionsSettingsSection.CopyCommandId);
        harness.Layout();

        Assert.Equal($"claude mcp add --transport http diffdino {endpoint}", _clipboard.Text);
        Assert.Equal("Command copied to the clipboard", Assert.Single(toasts).Intent.Message);
    }

    [Fact]
    public void Off_CopyDoesNothing()
    {
        using var harness = Mount(new AgentConnectionsSettingsSection());

        harness.ClickOn(AgentConnectionsSettingsSection.CopyCommandId);
        harness.Layout();

        Assert.Null(_clipboard.Text);
    }

    [Fact]
    public void Failed_ShowsTheReason()
    {
        _state.Value = new AgentConnectionState.Failed("port 5577 is taken");
        using var harness = Mount(new AgentConnectionsSettingsSection());

        Assert.True(HasText(harness.Render(), "Not listening: port 5577 is taken"));
    }

    [Fact]
    public void AnInvalidPort_ShowsTheError_AndIsNotSaved()
    {
        using var harness = Mount(new AgentConnectionsSettingsSection());

        harness.ClickOn(AgentConnectionsSettingsSection.PortInputId);
        harness.Type("x");
        harness.Layout();

        Assert.True(HasText(harness.Render(), "Enter a port between 1 and 65535."));
        Assert.Equal(5577, _settings.Value.Port);
    }

    [Fact]
    public void Badge_ShowsTheSessionCount_OnlyWhileAgentsAreConnected()
    {
        using var harness = Mount(new AgentConnectionsBadge());
        Assert.False(HasText(harness.Render(), "agent"));

        _state.Value = new AgentConnectionState.Listening(new Uri("http://127.0.0.1:5577/mcp/t"), 2);
        harness.Layout();
        Assert.True(HasText(harness.Render(), "2 agents"));

        _state.Value = new AgentConnectionState.Listening(new Uri("http://127.0.0.1:5577/mcp/t"), 1);
        harness.Layout();
        Assert.True(HasText(harness.Render(), "1 agent"));

        _state.Value = new AgentConnectionState.Off();
        harness.Layout();
        Assert.False(HasText(harness.Render(), "agent"));
    }
}

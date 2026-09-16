using GitBench.App;
using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Settings;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.AppUtils;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public sealed class SettingsDialogTests : IDisposable
{
    private readonly State<ThemeMode> _theme = new(ThemeMode.Dark);
    private readonly State<Locale> _locale = new(Locale.En);
    private readonly State<UiScale> _scale = new(UiScale.Default);
    private readonly State<bool> _cache = new(false);
    private readonly State<AgentConnectionSettings> _connections = new(new(false, 5577, null));
    private readonly State<AgentConnectionState> _connectionState = new(new AgentConnectionState.Off());
    private readonly FakeAssistantSessionStore _store = new();
    private readonly MessageBus _bus = new();
    private readonly KeyMap _keys = new();
    private readonly LocalizationService _loc;
    private readonly AssistantViewModel _chat;
    private bool _closed;
    private readonly SettingsDialog _dialog;

    public SettingsDialogTests()
    {
        _loc = new LocalizationService(_locale);
        _chat = new AssistantViewModel(_store, _loc, _bus);
        _dialog = new SettingsDialog { OnClose = () => _closed = true };
    }

    private GuiTestHarness Mount(int width = 800, int height = 700)
    {
        var harness = GuiTestHarness.Create(
            ctx => new Center { Child = _dialog.WithController<DialogKbmController>() }.BuildView(ctx),
            width: width, height: height, configure: Configure);
        harness.Layout();
        return harness;
    }

    private void Configure(Context ctx)
    {
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(_theme));
        ctx.AddService<ILocalizationService>(_loc);
        ctx.AddService(_theme);
        ctx.AddService(_locale);
        ctx.AddService(_scale);
        ctx.AddService(_cache);
        ctx.AddService(_connections);
        ctx.AddService(_connectionState);
        ctx.AddService<IAssistantSessionStore>(_store);
        ctx.AddService<IMessageBus>(_bus);
        ctx.AddService<IClipboard>(new Clipboard());
        ctx.AddService(_chat);
        ctx.AddService(_keys);
        ctx.AddService<IKeyMap>(_keys);
    }

    [Fact]
    public void TabsSeparateGeneralAgentAndConnections_WithoutNeedingARepository()
    {
        using var h = Mount();
        Assert.NotNull(h.Root.FindById(SettingsDialog.ThemePickerId));
        Assert.Null(h.Root.FindById(AssistantSettingsCard.KeyInputId));

        h.ClickOn(SettingsDialog.AgentTabId);
        h.Layout();
        Assert.NotNull(h.Root.FindById(AssistantSettingsCard.KeyInputId));
        Assert.False(_chat.IsOpen.Value);

        h.ClickOn(SettingsDialog.ConnectionsTabId);
        h.Layout();
        h.ClickOn(AgentConnectionsSettingsSection.EnabledId);
        Assert.True(_connections.Value.Enabled);
    }

    [Fact]
    public void SaveUsesTheSelectedProvidersOwnModelEndpointAndMaskedKey()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.AgentTabId);
        var editor = _dialog.State.Agent;
        editor.SetProviderDraft(AssistantProviders.Ollama.Id);
        editor.ModelDraft.Value = "local-custom-model";
        editor.BaseUrlDraft.Value = "http://localhost:11434/v1";
        editor.KeyDraft.Value = "private-gateway-token";
        h.Layout();

        var key = Assert.IsType<TextInputView>(h.Get(AssistantSettingsCard.KeyInputId));
        Assert.True(key.Masked);
        Assert.DoesNotContain(h.Render().Texts, t => t.Inputs.Text.Contains("private-gateway-token"));
        Assert.Empty(_store.Writes);

        h.ClickOn(AssistantSettingsCard.SaveId);
        Assert.Equal((AssistantProviders.Ollama.Id, "private-gateway-token"), Assert.Single(_store.Writes));
        Assert.Equal("local-custom-model", _store.Settings.Value.Model);
        Assert.Equal("http://localhost:11434/v1", _store.Settings.Value.BaseUrl);
        Assert.False(_closed);
        Assert.False(_chat.IsOpen.Value);
        Assert.Contains(h.Render().Texts, t => t.Inputs.Text.Contains("Selected: Ollama · local-custom-model"));

        editor.SetProviderDraft(AssistantProviders.Groq.Id);
        editor.ModelDraft.Value = "another-model";
        editor.KeyDraft.Value = "another-key";
        h.ClickOn(AssistantSettingsCard.SaveId);
        editor.SetProviderDraft(AssistantProviders.Ollama.Id);
        Assert.Equal("local-custom-model", editor.ModelDraft.Value);
        Assert.Equal("private-gateway-token", editor.KeyDraft.Value);
    }

    [Fact]
    public void ChangingTabsKeepsEdits_AndResetDoesNotTouchChatDrafts()
    {
        _store.SetSavedKey(AssistantProviders.Anthropic, "saved-key");
        _chat.OpenSettings.Execute();
        _chat.ModelDraft.Value = "unfinished-chat-edit";
        using var h = Mount();
        h.ClickOn(SettingsDialog.AgentTabId);
        _dialog.State.Agent.ModelDraft.Value = "unfinished-dialog-edit";

        h.ClickOn(SettingsDialog.GeneralTabId);
        h.ClickOn(SettingsDialog.AgentTabId);
        Assert.Equal("unfinished-dialog-edit", _dialog.State.Agent.ModelDraft.Value);

        h.ClickOn(AssistantSettingsCard.CancelId);
        Assert.Equal(string.Empty, _dialog.State.Agent.ModelDraft.Value);
        Assert.Equal("saved-key", _dialog.State.Agent.KeyDraft.Value);
        Assert.Equal("unfinished-chat-edit", _chat.ModelDraft.Value);
        Assert.True(_chat.IsOpen.Value);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public void EscapeDiscardsEdits_EnterDoesNotDismissOrSave()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.AgentTabId);
        _dialog.State.Agent.KeyDraft.Value = "unsaved";
        h.MoveTo(400, 350);
        h.PressKey(KeyboardKey.Enter);
        Assert.False(_closed);
        Assert.Empty(_store.Writes);

        h.PressKey(KeyboardKey.Escape);
        Assert.True(_closed);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public void KeyboardTabSearchesAndRebindsWithoutLeavingSettings_AndKeepsItsSearch()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.KeyboardTabId);
        h.Layout();
        h.ClickOn(KeyboardShortcutsDialog.SearchInputId);
        h.Type("Refresh");
        h.Layout();
        Assert.Equal("Refresh", Assert.IsType<TextInputView>(h.Get(KeyboardShortcutsDialog.SearchInputId)).Text);
        Assert.Contains(h.Render().Texts, text => text.Inputs.Text == "Refresh");
        Assert.DoesNotContain(h.Render().Texts, text => text.Inputs.Text == _loc.Strings.Value.ShortcutsCommandToggleRepoBar);

        h.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        h.PressKey(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift);
        h.Layout();
        Assert.Equal([new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift)],
            _keys.GesturesFor(KeyCommand.Refresh));
        Assert.False(_closed);

        h.ClickOn(SettingsDialog.GeneralTabId);
        Assert.Equal(SettingsPage.General, _dialog.State.Page.Value);
        h.ClickOn(SettingsDialog.KeyboardTabId);
        h.Layout();
        Assert.Equal("Refresh", Assert.IsType<TextInputView>(h.Get(KeyboardShortcutsDialog.SearchInputId)).Text);
        h.ClickOn(KeyboardShortcutsDialog.ResetId(KeyCommand.Refresh));
        Assert.True(_keys.IsDefault(KeyCommand.Refresh));
    }

    [Fact]
    public void LeavingTheKeyboardTabCancelsRecording()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.KeyboardTabId);
        h.Layout();
        h.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        h.ClickOn(SettingsDialog.AgentTabId);
        h.Layout();
        h.PressKey(KeyboardKey.F6);
        Assert.True(_keys.IsDefault(KeyCommand.Refresh));
        h.ClickOn(SettingsDialog.KeyboardTabId);
        Assert.DoesNotContain(h.Render().Texts, text => text.Inputs.Text.Contains("Press the new shortcut"));
        Assert.False(_closed);
    }

    [Fact]
    public void EscapeCancelsShortcutRecordingBeforeClosingSettings()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.KeyboardTabId);
        h.Layout();
        h.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        h.PressKey(KeyboardKey.Escape);
        Assert.False(_closed);
        Assert.True(_keys.IsDefault(KeyCommand.Refresh));
        h.Layout();
        h.MoveTo(400, 350);
        h.PressKey(KeyboardKey.Escape);
        Assert.True(_closed);
    }

    [Fact]
    public void KeyboardSearchAndResetStayVisibleWhileTheListScrolls()
    {
        _keys.Rebind(KeyCommand.Refresh, new KeyGesture(KeyboardKey.F6));
        using var h = Mount(640, 480);
        h.ClickOn(SettingsDialog.KeyboardTabId);
        h.Layout();
        h.Layout();
        var search = h.Get(KeyboardShortcutsDialog.SearchInputId).Position;
        var reset = h.Get(KeyboardShortcutsDialog.ResetAllId).Position;
        var firstRow = h.Get(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh)).Position;

        h.MoveTo(350, 200);
        h.Scroll(0, -5);
        h.Layout();

        Assert.Equal(search, h.Get(KeyboardShortcutsDialog.SearchInputId).Position);
        Assert.Equal(reset, h.Get(KeyboardShortcutsDialog.ResetAllId).Position);
        Assert.NotEqual(firstRow, h.Get(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh)).Position);
        Assert.True(search.Top <= 480 && reset.Bottom >= 0);
        h.ClickOn(KeyboardShortcutsDialog.ResetAllId);
        Assert.Empty(_keys.Overrides);
    }

    [Theory]
    [InlineData(800, 700)]
    [InlineData(640, 480)]
    public void AgentFieldsAndActionsStayInsideTheDialog(int width, int height)
    {
        using var h = Mount(width, height);
        h.ClickOn(SettingsDialog.AgentTabId);
        _dialog.State.Agent.SetProviderDraft(AssistantProviders.Ollama.Id);
        h.Layout();
        foreach (var id in new[] { SettingsDialog.AgentTabId, AssistantSettingsCard.ProviderId,
                     AssistantSettingsCard.ModelInputId, AssistantSettingsCard.BaseUrlInputId,
                     AssistantSettingsCard.KeyInputId, AssistantSettingsCard.SaveId })
        {
            var rect = h.Get(id).Position;
            Assert.True(rect.Width > 0 && rect.Height > 0, id);
            Assert.True(rect.Left >= 0 && rect.Right <= width && rect.Bottom >= 0 && rect.Top <= height,
                $"{id}: {rect} exceeds {width}x{height}");
        }
    }

    [Theory]
    [InlineData(Locale.En)]
    [InlineData(Locale.Es)]
    [InlineData(Locale.Ru)]
    [InlineData(Locale.Ja)]
    [InlineData(Locale.Ko)]
    [InlineData(Locale.ZhHans)]
    [InlineData(Locale.Ar)]
    public void TranslatedAgentActionsFitWithRealFontMetrics(Locale locale)
    {
        _locale.Value = locale;
        using var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(
            EmbeddedAssets.LoadBytes(typeof(LucideIcons).Assembly, "Inter-Italic.ttf"), 16);
        using var h = GuiTestHarness.CreateRaster(
            ctx => new Center { Child = _dialog }.BuildView(ctx), fonts, font,
            width: 640, height: 480, configure: Configure);
        h.ClickOn(SettingsDialog.AgentTabId);
        _dialog.State.Agent.SetProviderDraft(AssistantProviders.Ollama.Id);
        h.Layout();

        var reset = h.Get(AssistantSettingsCard.CancelId).Position;
        var save = h.Get(AssistantSettingsCard.SaveId).Position;
        Assert.True(reset.Right <= save.Left);
        Assert.True(reset.Left >= 40 && save.Right <= 600);
        Assert.True(save.Width > 0 && reset.Width > 0);
        AssertCategoryLabelsFit(h);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.9f)]
    [InlineData(1.1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void CategoryLabelsHaveRoomForTheirEntireText(float dpiScale)
    {
        using var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(
            EmbeddedAssets.LoadBytes(typeof(Context).Assembly, "Inter-Regular.ttf"), (int)MathF.Round(16 * dpiScale));
        using var h = GuiTestHarness.CreateRaster(
            ctx => new Center { Child = _dialog }.BuildView(ctx), fonts, font,
            width: 640, height: 480, configure: ctx =>
            {
                Configure(ctx);
                Assert.IsType<RasterCanvas>(ctx.Canvas).UpdateDpiScale(dpiScale);
            });
        h.Layout();
        AssertCategoryLabelsFit(h);
    }

    private static void AssertCategoryLabelsFit(GuiTestHarness h)
    {
        var drawn = new RecordingCanvas(new CanvasTextMeasurer(h.Context.Canvas));
        foreach (var id in new[] { SettingsDialog.GeneralTabId, SettingsDialog.KeyboardTabId, SettingsDialog.AgentTabId,
                     SettingsDialog.ConnectionsTabId })
        {
            var label = Assert.Single(h.Get(id).SelfAndDescendants().OfType<TextView>());
            Assert.True(label.Position.Width + 0.01f >= label.MeasureWidth(), label.Text);
            Assert.True(h.Get(id).Position.Left >= 40 && h.Get(id).Position.Right <= 600, label.Text);
            label.DrawSelf(drawn);
            Assert.Equal(label.Text, drawn.Texts.Last().Inputs.Text);
        }
    }

    private sealed class CanvasTextMeasurer(ICanvas canvas) : ITextMeasurer
    {
        public float MeasureTextWidth(ReadOnlySpan<char> text, TextStyle style) => canvas.MeasureTextWidth(text, style);
        public float MeasureTextPrefix(ReadOnlySpan<char> text, int length, TextStyle style) => canvas.MeasureTextPrefix(text, length, style);
        public float MeasureTextLineHeight(TextStyle style) => canvas.MeasureTextLineHeight(style);
    }

    [Fact]
    public void CategoryTabsDoNotDrawVerticalDividers()
    {
        using var h = Mount();
        h.ClickOn(SettingsDialog.ConnectionsTabId);
        foreach (var id in new[] { SettingsDialog.GeneralTabId, SettingsDialog.KeyboardTabId, SettingsDialog.AgentTabId,
                     SettingsDialog.ConnectionsTabId })
        {
            h.Canvas.Reset();
            h.Get(id).DrawSelf(h.Canvas);
            Assert.All(h.Canvas.Rects, rect =>
            {
                Assert.Equal(0, rect.Inputs.Style.BorderSize.Left.Value);
                Assert.Equal(0, rect.Inputs.Style.BorderSize.Right.Value);
            });
        }
    }

    public void Dispose()
    {
        _chat.Dispose();
        _store.Dispose();
        _loc.Dispose();
        _theme.Dispose();
        _locale.Dispose();
        _scale.Dispose();
        _cache.Dispose();
        _connections.Dispose();
        _connectionState.Dispose();
    }

    private sealed class Clipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }
}

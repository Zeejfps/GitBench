using System.Text.Json.Nodes;
using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>Agent presets: the arguments as typed and as passed, the harness each agent is run
/// with, the stored preference, and the settings page's edits.</summary>
public sealed class AgentPresetTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-agent-presets-");

    public void Dispose() => _dir.Dispose();

    private static AgentPreset Preset(AgentKind kind, AgentPermission permission = AgentPermission.Ask, params string[] arguments) =>
        new(new AgentPresetId("p1"), "Mine", kind, permission, arguments);

    [Fact]
    public void Arguments_SplitOnWhitespace_WithQuotesGrouping_AndBackslashesKept()
    {
        var parsed = Assert.IsType<AgentArgumentsParse.Ok>(
            AgentArguments.Parse("""--add-dir "C:\My Repos\x" --append-system-prompt 'say "hi"'  -c model=o3""", AgentKind.Codex));

        Assert.Equal(["--add-dir", @"C:\My Repos\x", "--append-system-prompt", "say \"hi\"", "-c", "model=o3"], parsed.Arguments);
    }

    [Theory]
    [InlineData("--model opus")]
    [InlineData("--flag")]
    [InlineData("\"has space\" '' plain")]
    public void Arguments_FormatReadsBackAsTheSameTokens(string text)
    {
        var tokens = AgentArguments.Split(text)!;

        Assert.Equal(tokens, AgentArguments.Split(AgentArguments.Format(tokens)));
    }

    [Fact]
    public void Arguments_WithBothQuotes_FormatReadsBack()
    {
        string[] tokens = ["both\"'quotes", "", "a b"];

        Assert.Equal(tokens, AgentArguments.Split(AgentArguments.Format(tokens)));
    }

    [Fact]
    public void Arguments_WithAQuoteLeftOpen_AreRefused() =>
        Assert.IsType<AgentArgumentsProblem.UnclosedQuote>(
            Assert.IsType<AgentArgumentsParse.Invalid>(AgentArguments.Parse("--model \"opus", AgentKind.Gemini)).Problem);

    [Theory]
    [InlineData("opus")]
    [InlineData("-m opus")]
    [InlineData("--model opus stray")]
    public void ClaudeArguments_ThatAreNotLongFlags_AreRefused(string text) =>
        Assert.IsType<AgentArgumentsProblem.NotAFlag>(
            Assert.IsType<AgentArgumentsParse.Invalid>(AgentArguments.Parse(text, AgentKind.ClaudeCode)).Problem);

    [Fact]
    public void ClaudeCode_GetsModelAndEffortAsOptions_AndOtherFlagsAsCliArguments_InTheSessionMeta()
    {
        var launch = AcpHarness.For(Preset(AgentKind.ClaudeCode, AgentPermission.Ask,
            "--model", "opus", "--effort=high", "--verbose", "--add-dir", "../other"));

        var harness = Assert.IsType<AcpLaunch.Ready>(launch).Harness;
        Assert.Equal(AcpHarness.ClaudeCode.Args, harness.Args);
        var options = JsonNode.Parse(harness.SessionMetaJson!)!["claudeCode"]!["options"]!;
        Assert.Equal("opus", options["model"]!.GetValue<string>());
        Assert.Equal("high", options["effort"]!.GetValue<string>());
        var extra = options["extraArgs"]!.AsObject();
        Assert.True(extra.ContainsKey("verbose"));
        Assert.Null(extra["verbose"]);
        Assert.Equal("../other", extra["add-dir"]!.GetValue<string>());
        Assert.Equal("Mine", harness.Label);
        Assert.Equal("opus", harness.Environment["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public void ClaudeCode_WithNoArguments_SendsNoMeta_AndLeavesTheModelToItsSettings()
    {
        var harness = Assert.IsType<AcpLaunch.Ready>(AcpHarness.For(AgentPreset.ClaudeCode)).Harness;

        Assert.Null(harness.SessionMetaJson);
        Assert.Empty(harness.Environment);
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Gemini)]
    public void OtherAgents_GetTheArgumentsAfterTheirAdapterCommand(AgentKind kind)
    {
        var harness = Assert.IsType<AcpLaunch.Ready>(AcpHarness.For(Preset(kind, AgentPermission.Ask, "--model", "x"))).Harness;

        Assert.Equal(["--model", "x"], harness.Args.TakeLast(2));
        Assert.Null(harness.SessionMetaJson);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, AgentPermission.Bypass, "default", "bypassPermissions")]
    [InlineData(AgentKind.ClaudeCode, AgentPermission.AcceptEdits, "default", "acceptEdits")]
    [InlineData(AgentKind.Codex, AgentPermission.Bypass, "read-only", "full-access")]
    [InlineData(AgentKind.Gemini, AgentPermission.AcceptEdits, "default", "autoEdit")]
    public void AChat_RunsInThePresetsMode_AndPairingAlwaysInTheAskingMode(
        AgentKind kind, AgentPermission permission, string asking, string chat)
    {
        var harness = Assert.IsType<AcpLaunch.Ready>(AcpHarness.For(Preset(kind, permission))).Harness;

        Assert.Equal(chat, harness.ModeFor(pairing: false));
        Assert.Equal(asking, harness.ModeFor(pairing: true));
    }

    [Fact]
    public void Presets_RoundTripThroughTheStore()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        var mine = new AgentPreset(AgentPresetId.New(), "Opus, no prompts", AgentKind.ClaudeCode, AgentPermission.Bypass, ["--model", "opus"]);
        PreferencesStore.Save(path, new Preferences { AgentPresets = [AgentPreset.Codex, mine] });

        var loaded = PreferencesStore.Load(path).AgentPresets;

        Assert.Equal([AgentPreset.Codex, mine], loaded);
    }

    [Fact]
    public void AStoredPresetThatCantRun_IsDropped_AndNoneLeftReadsAsTheBuiltIns()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, """
        {
          "agentPresets": [
            { "id": "a", "name": "Bad agent", "agent": "Copilot", "permission": "Ask", "arguments": [] },
            { "id": "b", "name": "Bad flags", "agent": "ClaudeCode", "permission": "Ask", "arguments": ["opus"] },
            { "id": "c", "name": " ", "agent": "Codex", "permission": "Ask", "arguments": [] }
          ]
        }
        """);

        Assert.Equal(AgentPreset.BuiltIn, PreferencesStore.Load(path).AgentPresets);
    }

    [Fact]
    public void AFileFromBeforePresets_ReadsAsTheBuiltIns_AndKeepsItsPick()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, """{ "chatAgent": "codex" }""");

        var loaded = PreferencesStore.Load(path);

        Assert.Equal(AgentPreset.BuiltIn, loaded.AgentPresets);
        Assert.Equal(AgentPreset.Codex.Id.Value, loaded.ChatAgent);
    }

    private (AgentPresetsSettingsViewModel Vm, PreferencesService Preferences, MessageBus Bus) Settings()
    {
        var preferences = new PreferencesService(new Preferences(), Path.Combine(_dir.Path, "prefs.json"));
        var bus = new MessageBus();
        return (new AgentPresetsSettingsViewModel(preferences, bus, new LocalizationService(new State<Locale>(Locale.En))), preferences, bus);
    }

    [Fact]
    public void AddingAndEditingAPreset_WritesThrough()
    {
        var (vm, preferences, _) = Settings();
        using var _vm = vm;
        using var _preferences = preferences;

        vm.Add.Execute();
        vm.Name.Value = "Opus";
        vm.Permission.Value = AgentPermission.Bypass;
        vm.Arguments.Value = "--model opus";

        var added = preferences.Current.AgentPresets[^1];
        Assert.Equal(4, preferences.Current.AgentPresets.Count);
        Assert.Equal("Opus", added.Name);
        Assert.Equal(AgentPermission.Bypass, added.Permission);
        Assert.Equal(["--model", "opus"], added.Arguments);
    }

    [Fact]
    public void ArgumentsThatDontParse_ShowWhy_AndSaveNothing()
    {
        var (vm, preferences, _) = Settings();
        using var _vm = vm;
        using var _preferences = preferences;

        vm.Arguments.Value = "opus";

        Assert.NotNull(vm.ArgumentsStatus.Value);
        Assert.Equal(AgentPreset.ClaudeCode, preferences.Current.AgentPresets[0]);

        vm.Kind.Value = AgentKind.Gemini;

        Assert.Null(vm.ArgumentsStatus.Value);
        Assert.Equal(["opus"], preferences.Current.AgentPresets[0].Arguments);
    }

    [Fact]
    public void SelectingAnotherPreset_LoadsItsFields_WithoutWritingTheOldOnesOverIt()
    {
        var (vm, preferences, _) = Settings();
        using var _vm = vm;
        using var _preferences = preferences;

        vm.Select(AgentPreset.Gemini.Id);

        Assert.Equal("Gemini CLI", vm.Name.Value);
        Assert.Equal(AgentKind.Gemini, vm.Kind.Value);
        Assert.Equal(AgentPreset.BuiltIn, preferences.Current.AgentPresets);
    }

    [Fact]
    public void DeletingAPreset_OffersUndo_AndTheLastOneStays()
    {
        var (vm, preferences, bus) = Settings();
        using var _vm = vm;
        using var _preferences = preferences;
        ShowToastMessage? toast = null;
        bus.Subscribe<ShowToastMessage>(m => toast = m);

        vm.Delete(AgentPreset.Codex.Id);

        Assert.Equal([AgentPreset.ClaudeCode, AgentPreset.Gemini], preferences.Current.AgentPresets);
        toast!.Value.Intent.Action!.Invoke();
        Assert.Equal(AgentPreset.BuiltIn, preferences.Current.AgentPresets);

        vm.Delete(AgentPreset.Codex.Id);
        vm.Delete(AgentPreset.Gemini.Id);
        Assert.False(vm.CanDelete.Value);
        vm.Delete(AgentPreset.ClaudeCode.Id);
        Assert.Equal([AgentPreset.ClaudeCode], preferences.Current.AgentPresets);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.LocalChanges;
using GitBench.Infrastructure;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui.Desktop;

namespace GitBench.App;

public static class PreferencesStore
{
    private const int CurrentSchemaVersion = 1;

    internal sealed class FileShape
    {
        public int? SchemaVersion { get; set; }
        public ThemeMode? Theme { get; set; } = ThemeMode.Dark;

        // Stored as a string rather than a Locale so an unrecognized value (a locale removed in a
        // later version, or a hand-edited file) parses leniently instead of throwing inside the
        // enum converter — which would discard every other preference along with it.
        public string? Language { get; set; } = nameof(Locale.En);

        // A raw float, snapped to the nearest offered scale as it is parsed. Stored as a number rather
        // than a named rung on purpose: an enum converter throws on a value it doesn't recognize, and
        // that throw is caught below and discards every other preference along with it.
        public float? UiScale { get; set; } = 1f;
        public float? EditorFontSize { get; set; }
        public int? WindowWidth { get; set; } = 1400;
        public int? WindowHeight { get; set; } = 900;

        // Null (the default) means "never placed" — the window is centered. Stored verbatim,
        // including negatives, since a saved spot may sit on a monitor left of the primary.
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public int? ReviewWindowWidth { get; set; } = 1100;
        public int? ReviewWindowHeight { get; set; } = 800;
        public int? ReviewWindowX { get; set; }
        public int? ReviewWindowY { get; set; }
        public float? RepoBarWidth { get; set; } = 220f;
        public bool? RepoBarCollapsed { get; set; } = false;
        public float? BranchesWidth { get; set; } = 220f;
        public float? PairingPanelWidth { get; set; }
        public float? PairingChatSplitFraction { get; set; }
        public string? PairingTerminalCommand { get; set; }
        public string? ChatAgent { get; set; }
        public List<AgentPresetShape>? AgentPresets { get; set; }
        public float? CommitDetailsWidth { get; set; } = 380f;
        public float? FileBrowserWidth { get; set; } = 260f;
        public float? CommitDetailsSplitFraction { get; set; } = 2f / 3f;
        public FileViewMode? FileViewMode { get; set; } = Features.LocalChanges.FileViewMode.Flat;
        public WorkingChangesLayout? WorkingChangesLayout { get; set; } = Features.LocalChanges.WorkingChangesLayout.Diff;
        public bool? HideRemoteOnlyBranches { get; set; } = false;
        public bool? EnableUntrackedCache { get; set; } = false;

        // Read, never written: what a file from before roles had their own models carries — the
        // one selected provider, the flat model and endpoint an even older file kept for it, and
        // the per-provider list that replaced the flat pair. ReadAssistantModels and
        // ReadAssistantEndpoints fold them into the per-role and per-provider lists.
        public string? AssistantProviderId { get; set; }
        public string? AssistantModel { get; set; }
        public string? AssistantBaseUrl { get; set; }
        public List<AssistantProviderShape>? AssistantProviderChoices { get; set; }

        // Role and provider ids as free text: an entry this version does not know drops that one
        // entry, not the whole file.
        public List<AssistantModelShape>? AssistantModels { get; set; }
        public List<AssistantEndpointShape>? AssistantEndpoints { get; set; }


        public List<KeyBindingShape>? KeyBindings { get; set; }

        public bool? AgentConnectionsEnabled { get; set; } = false;
        public int? AgentConnectionsPort { get; set; } = 5577;

        // Free text, parsed back through McpPathToken: a hand-edited token that could not sit in
        // a URL is dropped and regenerated, not carried into the endpoint.
        public string? AgentConnectionsToken { get; set; }
    }

    internal sealed class AssistantProviderShape
    {
        public string? Id { get; set; }
        public string? Model { get; set; }
        public string? BaseUrl { get; set; }
    }

    internal sealed class AssistantModelShape
    {
        public string? Role { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
    }

    internal sealed class AssistantEndpointShape
    {
        public string? Id { get; set; }
        public string? BaseUrl { get; set; }
    }

    // Agent and permission as free text: a preset this version can't run drops that one preset,
    // not the whole file.
    internal sealed class AgentPresetShape
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Agent { get; set; }
        public string? Permission { get; set; }
        public List<string>? Arguments { get; set; }
    }

    // Both as free text: a command or key this version no longer knows drops that one entry, not
    // the whole file.
    internal sealed class KeyBindingShape
    {
        public string? Command { get; set; }
        public List<string>? Keys { get; set; }
    }

    public static Preferences Load(string path)
    {
        if (!File.Exists(path))
            return Preferences.Default;

        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, PreferencesJsonContext.Default.FileShape);
            if (file is null)
                return Preferences.Default;

            var defaults = Preferences.Default;
            return new Preferences
            {
                Theme = file.Theme ?? defaults.Theme,
                Language = ParseLocale(file.Language) ?? defaults.Language,
                UiScale = file.UiScale is { } uiScale ? new UiScale(uiScale) : defaults.UiScale,
                EditorFontSize = file.EditorFontSize is { } editorFontSize ? new EditorFontSize(editorFontSize) : defaults.EditorFontSize,
                WindowWidth = file.WindowWidth is > 0 ? file.WindowWidth.Value : defaults.WindowWidth,
                WindowHeight = file.WindowHeight is > 0 ? file.WindowHeight.Value : defaults.WindowHeight,
                WindowX = file.WindowX,
                WindowY = file.WindowY,
                ReviewWindowWidth = file.ReviewWindowWidth is > 0 ? file.ReviewWindowWidth.Value : defaults.ReviewWindowWidth,
                ReviewWindowHeight = file.ReviewWindowHeight is > 0 ? file.ReviewWindowHeight.Value : defaults.ReviewWindowHeight,
                ReviewWindowX = file.ReviewWindowX,
                ReviewWindowY = file.ReviewWindowY,
                RepoBarWidth = file.RepoBarWidth is > 0 ? file.RepoBarWidth.Value : defaults.RepoBarWidth,
                RepoBarCollapsed = file.RepoBarCollapsed ?? defaults.RepoBarCollapsed,
                BranchesWidth = file.BranchesWidth is > 0 ? file.BranchesWidth.Value : defaults.BranchesWidth,
                PairingPanelWidth = file.PairingPanelWidth is > 0 ? file.PairingPanelWidth.Value : defaults.PairingPanelWidth,
                PairingChatSplitFraction = file.PairingChatSplitFraction is > 0 ? file.PairingChatSplitFraction.Value : defaults.PairingChatSplitFraction,
                PairingTerminalCommand = string.IsNullOrWhiteSpace(file.PairingTerminalCommand)
                                         || file.PairingTerminalCommand == Features.Pairing.TerminalAgentCommand.LegacyClaudeCode
                    ? defaults.PairingTerminalCommand
                    : file.PairingTerminalCommand,
                ChatAgent = string.IsNullOrWhiteSpace(file.ChatAgent) ? null : file.ChatAgent,
                AgentPresets = ReadAgentPresets(file),
                CommitDetailsWidth = file.CommitDetailsWidth is > 0 ? file.CommitDetailsWidth.Value : defaults.CommitDetailsWidth,
                FileBrowserWidth = file.FileBrowserWidth is > 0 ? file.FileBrowserWidth.Value : defaults.FileBrowserWidth,
                CommitDetailsSplitFraction = file.CommitDetailsSplitFraction is > 0 ? file.CommitDetailsSplitFraction.Value : defaults.CommitDetailsSplitFraction,
                FileViewMode = file.FileViewMode ?? defaults.FileViewMode,
                WorkingChangesLayout = file.WorkingChangesLayout ?? defaults.WorkingChangesLayout,
                HideRemoteOnlyBranches = file.HideRemoteOnlyBranches ?? defaults.HideRemoteOnlyBranches,
                EnableUntrackedCache = file.EnableUntrackedCache ?? defaults.EnableUntrackedCache,
                AssistantModels = ReadAssistantModels(file),
                AssistantEndpoints = ReadAssistantEndpoints(file),
                KeyBindings = ReadKeyBindings(file),
                AgentConnectionsEnabled = file.AgentConnectionsEnabled ?? defaults.AgentConnectionsEnabled,
                AgentConnectionsPort = file.AgentConnectionsPort is >= 1 and <= 65535
                    ? file.AgentConnectionsPort.Value
                    : defaults.AgentConnectionsPort,
                AgentConnectionsToken = McpPathToken.TryParse(file.AgentConnectionsToken, out var token) ? token : null,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load preferences from {path}: {ex.Message}");
            return Preferences.Default;
        }
    }

    public static void Save(string path, Preferences preferences)
    {
        var file = new FileShape
        {
            SchemaVersion = CurrentSchemaVersion,
            Theme = preferences.Theme,
            Language = preferences.Language.ToString(),
            UiScale = preferences.UiScale.Factor,
            EditorFontSize = preferences.EditorFontSize.Points,
            WindowWidth = preferences.WindowWidth,
            WindowHeight = preferences.WindowHeight,
            WindowX = preferences.WindowX,
            WindowY = preferences.WindowY,
            ReviewWindowWidth = preferences.ReviewWindowWidth,
            ReviewWindowHeight = preferences.ReviewWindowHeight,
            ReviewWindowX = preferences.ReviewWindowX,
            ReviewWindowY = preferences.ReviewWindowY,
            RepoBarWidth = preferences.RepoBarWidth,
            RepoBarCollapsed = preferences.RepoBarCollapsed,
            BranchesWidth = preferences.BranchesWidth,
            PairingPanelWidth = preferences.PairingPanelWidth,
            PairingChatSplitFraction = preferences.PairingChatSplitFraction,
            PairingTerminalCommand = preferences.PairingTerminalCommand,
            ChatAgent = preferences.ChatAgent,
            AgentPresets = preferences.AgentPresets
                .Select(p => new AgentPresetShape
                {
                    Id = p.Id.Value,
                    Name = p.Name,
                    Agent = p.Kind.ToString(),
                    Permission = p.Permission.ToString(),
                    Arguments = [.. p.Arguments],
                })
                .ToList(),
            CommitDetailsWidth = preferences.CommitDetailsWidth,
            FileBrowserWidth = preferences.FileBrowserWidth,
            CommitDetailsSplitFraction = preferences.CommitDetailsSplitFraction,
            FileViewMode = preferences.FileViewMode,
            WorkingChangesLayout = preferences.WorkingChangesLayout,
            HideRemoteOnlyBranches = preferences.HideRemoteOnlyBranches,
            EnableUntrackedCache = preferences.EnableUntrackedCache,
            AssistantModels = preferences.AssistantModels
                .Select(m => new AssistantModelShape { Role = m.Role, Provider = m.ProviderId, Model = m.Model })
                .ToList(),
            AssistantEndpoints = preferences.AssistantEndpoints
                .Select(e => new AssistantEndpointShape { Id = e.ProviderId, BaseUrl = e.BaseUrl })
                .ToList(),
            KeyBindings = preferences.KeyBindings
                .Select(b => new KeyBindingShape
                {
                    Command = b.Command.ToString(),
                    Keys = b.Triggers.Select(t => t.Serialize()).ToList(),
                })
                .ToList(),
            AgentConnectionsEnabled = preferences.AgentConnectionsEnabled,
            AgentConnectionsPort = preferences.AgentConnectionsPort,
            AgentConnectionsToken = preferences.AgentConnectionsToken?.Value,
        };
        var json = JsonSerializer.Serialize(file, PreferencesJsonContext.Default.FileShape);
        AtomicFile.WriteAllText(path, json);
    }

    // A preset survives only whole: an id not already taken, a name, a known agent and permission,
    // and arguments that agent can take. A file with none left reads as the built-in presets.
    private static IReadOnlyList<AgentPreset> ReadAgentPresets(FileShape file)
    {
        var presets = new List<AgentPreset>();
        foreach (var entry in file.AgentPresets ?? [])
        {
            if (entry.Id is not { Length: > 0 } id || presets.Any(p => p.Id.Value == id)) continue;
            if (entry.Name?.Trim() is not { Length: > 0 } name) continue;
            if (!Enum.TryParse<AgentKind>(entry.Agent, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) continue;
            if (!Enum.TryParse<AgentPermission>(entry.Permission, ignoreCase: true, out var permission) || !Enum.IsDefined(permission)) continue;
            var arguments = entry.Arguments ?? [];
            if (arguments.Any(a => a is null) || AgentArguments.Check(arguments, kind) is not null) continue;
            presets.Add(new AgentPreset(new AgentPresetId(id), name, kind, permission, arguments));
        }

        return presets.Count > 0 ? presets : AgentPreset.BuiltIn;
    }

    // An entry survives only whole: a known command with at least one readable key. A command that
    // is known but whose keys are not falls back to its defaults rather than to no key at all.
    private static IReadOnlyList<KeyBinding> ReadKeyBindings(FileShape file)
    {
        var bindings = new List<KeyBinding>();
        foreach (var entry in file.KeyBindings ?? [])
        {
            if (!Enum.TryParse<KeyCommand>(entry.Command, ignoreCase: true, out var command) || !Enum.IsDefined(command))
                continue;
            var triggers = new List<KeyTrigger>();
            foreach (var text in entry.Keys ?? [])
                if (KeyTrigger.TryParse(text, out var trigger))
                    triggers.Add(trigger);
            if (triggers.Count > 0)
                bindings.Add(new KeyBinding(command, triggers));
        }

        return bindings;
    }

    // A file from before roles had their own models carries one selected provider, and either a
    // flat model for it or a per-provider list it appears in. Every role is read as running on that
    // provider and model, so the model the assistant was configured with is still the model each
    // of its jobs runs on.
    private static IReadOnlyList<AssistantModelPreference> ReadAssistantModels(FileShape file)
    {
        if (file.AssistantModels is { } entries)
            return entries
                .Where(e => e.Role is { Length: > 0 } && e.Provider is { Length: > 0 })
                .Select(e => new AssistantModelPreference(e.Role!, e.Provider!, e.Model))
                .ToArray();

        if (file.AssistantProviderId is not { Length: > 0 } selected) return [];
        var model = file.AssistantModel
            ?? LegacyChoiceFor(file, selected)?.Model;
        return AssistantRoles.All
            .Select(role => new AssistantModelPreference(AssistantRoles.Id(role), selected, model))
            .ToArray();
    }

    // Endpoints were kept beside the model per provider, and before that flat for the selected
    // provider; either is read as that provider's endpoint.
    private static IReadOnlyList<AssistantEndpointPreference> ReadAssistantEndpoints(FileShape file)
    {
        if (file.AssistantEndpoints is { } entries)
            return entries
                .Where(e => e.Id is { Length: > 0 } && e.BaseUrl is { Length: > 0 })
                .Select(e => new AssistantEndpointPreference(e.Id!, e.BaseUrl!))
                .ToArray();

        var endpoints = new List<AssistantEndpointPreference>();
        foreach (var entry in file.AssistantProviderChoices ?? [])
            if (entry.Id is { Length: > 0 } id && entry.BaseUrl is { Length: > 0 } baseUrl)
                endpoints.Add(new AssistantEndpointPreference(id, baseUrl));

        if (file.AssistantProviderId is { Length: > 0 } selected
            && file.AssistantBaseUrl is { Length: > 0 } flat
            && LegacyChoiceFor(file, selected) is null)
            endpoints.Add(new AssistantEndpointPreference(selected, flat));
        return endpoints;
    }

    private static AssistantProviderShape? LegacyChoiceFor(FileShape file, string providerId) =>
        (file.AssistantProviderChoices ?? [])
            .FirstOrDefault(c => string.Equals(c.Id, providerId, StringComparison.OrdinalIgnoreCase));

    private static Locale? ParseLocale(string? value) =>
        Enum.TryParse<Locale>(value, ignoreCase: true, out var locale) && Enum.IsDefined(locale)
            ? locale
            : null;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PreferencesStore.FileShape))]
internal partial class PreferencesJsonContext : JsonSerializerContext;

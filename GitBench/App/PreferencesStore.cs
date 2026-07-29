using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Features.LocalChanges;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;

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
        public float? CommitDetailsWidth { get; set; } = 380f;
        public float? CommitDetailsSplitFraction { get; set; } = 2f / 3f;
        public FileViewMode? FileViewMode { get; set; } = Features.LocalChanges.FileViewMode.Flat;
        public WorkingChangesLayout? WorkingChangesLayout { get; set; } = Features.LocalChanges.WorkingChangesLayout.Diff;
        public bool? HideRemoteOnlyBranches { get; set; } = false;
        public bool? EnableUntrackedCache { get; set; } = false;

        // Stored as free text rather than an enum: an unknown provider id resolves back to the
        // default instead of discarding every other preference with it.
        public string? AssistantProviderId { get; set; }

        // Read, never written: the flat pair a pre-list file carries, which ReadAssistantChoices
        // folds into the per-provider list.
        public string? AssistantModel { get; set; }
        public string? AssistantBaseUrl { get; set; }

        public List<AssistantProviderShape>? AssistantProviderChoices { get; set; }

        public float? AssistantPanelWidth { get; set; } = 380f;
        public float? AssistantPanelHeight { get; set; } = 460f;

        // Null (the default) means "never moved" — the panel rests in the top trailing corner.
        public float? AssistantPanelX { get; set; }
        public float? AssistantPanelY { get; set; }
    }

    internal sealed class AssistantProviderShape
    {
        public string? Id { get; set; }
        public string? Model { get; set; }
        public string? BaseUrl { get; set; }
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
                CommitDetailsWidth = file.CommitDetailsWidth is > 0 ? file.CommitDetailsWidth.Value : defaults.CommitDetailsWidth,
                CommitDetailsSplitFraction = file.CommitDetailsSplitFraction is > 0 ? file.CommitDetailsSplitFraction.Value : defaults.CommitDetailsSplitFraction,
                FileViewMode = file.FileViewMode ?? defaults.FileViewMode,
                WorkingChangesLayout = file.WorkingChangesLayout ?? defaults.WorkingChangesLayout,
                HideRemoteOnlyBranches = file.HideRemoteOnlyBranches ?? defaults.HideRemoteOnlyBranches,
                EnableUntrackedCache = file.EnableUntrackedCache ?? defaults.EnableUntrackedCache,
                AssistantProviderId = file.AssistantProviderId,
                AssistantProviderPreferences = ReadAssistantChoices(file),
                AssistantPanelWidth = file.AssistantPanelWidth is > 0 ? file.AssistantPanelWidth.Value : defaults.AssistantPanelWidth,
                AssistantPanelHeight = file.AssistantPanelHeight is > 0 ? file.AssistantPanelHeight.Value : defaults.AssistantPanelHeight,
                AssistantPanelX = file.AssistantPanelX,
                AssistantPanelY = file.AssistantPanelY,
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
            CommitDetailsWidth = preferences.CommitDetailsWidth,
            CommitDetailsSplitFraction = preferences.CommitDetailsSplitFraction,
            FileViewMode = preferences.FileViewMode,
            WorkingChangesLayout = preferences.WorkingChangesLayout,
            HideRemoteOnlyBranches = preferences.HideRemoteOnlyBranches,
            EnableUntrackedCache = preferences.EnableUntrackedCache,
            AssistantProviderId = preferences.AssistantProviderId,
            AssistantProviderChoices = preferences.AssistantProviderPreferences
                .Select(c => new AssistantProviderShape { Id = c.ProviderId, Model = c.Model, BaseUrl = c.BaseUrl })
                .ToList(),
            AssistantPanelWidth = preferences.AssistantPanelWidth,
            AssistantPanelHeight = preferences.AssistantPanelHeight,
            AssistantPanelX = preferences.AssistantPanelX,
            AssistantPanelY = preferences.AssistantPanelY,
        };
        var json = JsonSerializer.Serialize(file, PreferencesJsonContext.Default.FileShape);
        AtomicFile.WriteAllText(path, json);
    }

    // Before this list existed the model and endpoint were kept flat, for whichever provider was
    // selected. A file written then still carries them, so they are read as that provider's entry
    // rather than dropped: a model configured before the app remembered them per provider is still
    // the model that provider was configured with.
    private static IReadOnlyList<AssistantProviderPreference> ReadAssistantChoices(FileShape file)
    {
        var choices = new List<AssistantProviderPreference>();
        foreach (var entry in file.AssistantProviderChoices ?? [])
            if (entry.Id is { Length: > 0 } id)
                choices.Add(new AssistantProviderPreference(id, entry.Model, entry.BaseUrl));

        var selected = file.AssistantProviderId;
        if (selected is not { Length: > 0 }) return choices;
        if (file.AssistantModel is null && file.AssistantBaseUrl is null) return choices;
        if (choices.Any(c => string.Equals(c.ProviderId, selected, StringComparison.OrdinalIgnoreCase)))
            return choices;

        choices.Add(new AssistantProviderPreference(selected, file.AssistantModel, file.AssistantBaseUrl));
        return choices;
    }

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

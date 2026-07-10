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
        public float? RepoBarWidth { get; set; } = 220f;
        public float? BranchesWidth { get; set; } = 220f;
        public float? CommitDetailsWidth { get; set; } = 380f;
        public float? CommitDetailsSplitFraction { get; set; } = 2f / 3f;
        public FileViewMode? FileViewMode { get; set; } = Features.LocalChanges.FileViewMode.Flat;
        public WorkingChangesLayout? WorkingChangesLayout { get; set; } = Features.LocalChanges.WorkingChangesLayout.List;
        public bool? HideRemoteOnlyBranches { get; set; } = false;
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
                RepoBarWidth = file.RepoBarWidth is > 0 ? file.RepoBarWidth.Value : defaults.RepoBarWidth,
                BranchesWidth = file.BranchesWidth is > 0 ? file.BranchesWidth.Value : defaults.BranchesWidth,
                CommitDetailsWidth = file.CommitDetailsWidth is > 0 ? file.CommitDetailsWidth.Value : defaults.CommitDetailsWidth,
                CommitDetailsSplitFraction = file.CommitDetailsSplitFraction is > 0 ? file.CommitDetailsSplitFraction.Value : defaults.CommitDetailsSplitFraction,
                FileViewMode = file.FileViewMode ?? defaults.FileViewMode,
                WorkingChangesLayout = file.WorkingChangesLayout ?? defaults.WorkingChangesLayout,
                HideRemoteOnlyBranches = file.HideRemoteOnlyBranches ?? defaults.HideRemoteOnlyBranches,
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
            RepoBarWidth = preferences.RepoBarWidth,
            BranchesWidth = preferences.BranchesWidth,
            CommitDetailsWidth = preferences.CommitDetailsWidth,
            CommitDetailsSplitFraction = preferences.CommitDetailsSplitFraction,
            FileViewMode = preferences.FileViewMode,
            WorkingChangesLayout = preferences.WorkingChangesLayout,
            HideRemoteOnlyBranches = preferences.HideRemoteOnlyBranches,
        };
        var json = JsonSerializer.Serialize(file, PreferencesJsonContext.Default.FileShape);
        AtomicFile.WriteAllText(path, json);
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

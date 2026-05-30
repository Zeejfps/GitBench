using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitGui;

public static class PreferencesStore
{
    private const int CurrentSchemaVersion = 1;

    internal sealed class FileShape
    {
        public int? SchemaVersion { get; set; }
        public ThemeMode? Theme { get; set; } = ThemeMode.Dark;
        public int? WindowWidth { get; set; } = 1400;
        public int? WindowHeight { get; set; } = 900;
        public float? RepoBarWidth { get; set; } = 220f;
        public float? BranchesWidth { get; set; } = 220f;
        public float? CommitDetailsWidth { get; set; } = 380f;
        public FileViewMode? FileViewMode { get; set; } = GitGui.FileViewMode.Flat;
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
                WindowWidth = file.WindowWidth is > 0 ? file.WindowWidth.Value : defaults.WindowWidth,
                WindowHeight = file.WindowHeight is > 0 ? file.WindowHeight.Value : defaults.WindowHeight,
                RepoBarWidth = file.RepoBarWidth is > 0 ? file.RepoBarWidth.Value : defaults.RepoBarWidth,
                BranchesWidth = file.BranchesWidth is > 0 ? file.BranchesWidth.Value : defaults.BranchesWidth,
                CommitDetailsWidth = file.CommitDetailsWidth is > 0 ? file.CommitDetailsWidth.Value : defaults.CommitDetailsWidth,
                FileViewMode = file.FileViewMode ?? defaults.FileViewMode,
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
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new FileShape
        {
            SchemaVersion = CurrentSchemaVersion,
            Theme = preferences.Theme,
            WindowWidth = preferences.WindowWidth,
            WindowHeight = preferences.WindowHeight,
            RepoBarWidth = preferences.RepoBarWidth,
            BranchesWidth = preferences.BranchesWidth,
            CommitDetailsWidth = preferences.CommitDetailsWidth,
            FileViewMode = preferences.FileViewMode,
        };
        var json = JsonSerializer.Serialize(file, PreferencesJsonContext.Default.FileShape);
        File.WriteAllText(path, json);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PreferencesStore.FileShape))]
internal partial class PreferencesJsonContext : JsonSerializerContext;

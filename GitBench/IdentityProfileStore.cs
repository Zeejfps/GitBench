using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitBench;

public static class IdentityProfileStore
{
    private const int CurrentSchemaVersion = 1;

    internal sealed class FileShape
    {
        public int? SchemaVersion { get; set; }
        public List<IdentityProfile>? Profiles { get; set; }
    }

    public static IReadOnlyList<IdentityProfile> Load(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<IdentityProfile>();

        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, IdentityProfileJsonContext.Default.FileShape);
            return file?.Profiles is { } list ? list : Array.Empty<IdentityProfile>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load identity profiles from {path}: {ex.Message}");
            return Array.Empty<IdentityProfile>();
        }
    }

    public static void Save(string path, IReadOnlyList<IdentityProfile> profiles)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new FileShape
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = profiles as List<IdentityProfile> ?? profiles.ToList(),
        };
        var json = JsonSerializer.Serialize(file, IdentityProfileJsonContext.Default.FileShape);
        // Atomic temp-then-rename so a crash mid-write can't leave a truncated file that fails to
        // parse on next launch (silently dropping every profile). Mirrors PreferencesStore.Save.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdentityProfileStore.FileShape))]
internal partial class IdentityProfileJsonContext : JsonSerializerContext;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitBench.App;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// Remembers plans on disk so reading the same diff twice costs nothing.
/// </summary>
/// <remarks>
/// Abridging a change takes a minute and a large fraction of a model context, which is affordable
/// once and absurd every time a reviewer scrolls back to a file. The key covers everything that
/// decides the answer — the diff's own content, the model, and the whole prompt surface — so a hit
/// is always a plan the current rules would produce, and a miss is always a real change of input.
///
/// What is stored is the plan, not the compiled overlay: loading recompiles it, which re-runs every
/// invariant. A plan that a later build's compiler rejects is dropped rather than trusted.
/// </remarks>
internal sealed class ReadingPlanStore
{
    private const string Folder = "reading-plans";
    private const int MaxEntries = 400;

    private readonly string _root;

    public ReadingPlanStore(string? root = null) => _root = root ?? AppPaths.AppDataPath(Folder);

    public static string Key(ReadingRowIndex index, string model, string surfaceHash)
    {
        var bytes = Encoding.UTF8.GetBytes($"{surfaceHash}\0{model}\0{index.Render()}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public ReadingOverlay? Load(string key, ReadingRowIndex index)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (ReadingPlanToolProtocol.Parse(doc.RootElement, out _) is not { } plan) return null;
            return ReadingPlanCompiler.Compile(index, plan).Overlay;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string key, ReadingPlan plan)
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(PathFor(key), Serialize(plan));
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is a slower next run, not a failure worth reporting.
        }
    }

    private string PathFor(string key) => Path.Combine(_root, key + ".json");

    // Oldest-first eviction, run after a write so the folder cannot grow without bound on a machine
    // that reviews a lot of branches.
    private void Trim()
    {
        var files = new DirectoryInfo(_root).GetFiles("*.json");
        if (files.Length <= MaxEntries) return;
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (var i = 0; i < files.Length - MaxEntries; i++)
        {
            try { files[i].Delete(); }
            catch (IOException) { }
        }
    }

    internal static string Serialize(ReadingPlan plan)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, ToolJson.WriterOptions))
        {
            w.WriteStartObject();
            WriteRanges(w, "remove", plan.Remove.Select(r => (r.StartRow, r.EndRow)));
            WriteRanges(w, "fold", plan.Fold.Select(f => (f.StartRow, f.EndRow)));
            w.WriteStartArray("replace");
            foreach (var e in plan.Replace)
            {
                w.WriteStartObject();
                w.WriteNumber("row", e.Row);
                w.WriteString("old", e.Old);
                w.WriteString("new", e.New);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (plan.Summary is { } summary) w.WriteString("summary", summary);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteRanges(Utf8JsonWriter w, string name, IEnumerable<(int Start, int End)> ranges)
    {
        w.WriteStartArray(name);
        foreach (var (start, end) in ranges)
        {
            w.WriteStartObject();
            w.WriteNumber("start_row", start);
            w.WriteNumber("end_row", end);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

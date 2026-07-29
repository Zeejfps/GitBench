using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GitBench.Features.Assistant.Tools;

/// Builds the compact JSON a tool hands back, and reads arguments off the model's input object.
internal static class ToolJson
{
    /// <summary>Shared by every writer that emits assistant JSON: the relaxed encoder keeps prose in
    /// a tool result or a request body readable rather than escaping every non-ASCII character.</summary>
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string? String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The string entries of an array argument. Anything that is not an array of strings
    /// comes back empty, so the tool reports the argument as missing rather than half-reading it.</summary>
    public static IReadOnlyList<string> Strings(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var text = entry.GetString();
            if (!string.IsNullOrWhiteSpace(text)) items.Add(text);
        }

        return items;
    }

    /// <summary>
    /// The arguments as the user approves them: every property on its own line, strings printed as
    /// themselves rather than as JSON, so a commit message reads like a commit message. Empty when
    /// the tool takes no arguments.
    /// </summary>
    public static string Describe(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return args.ToString();

        var builder = new StringBuilder();
        foreach (var property in args.EnumerateObject())
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(property.Name).Append(": ");
            builder.Append(property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString());
        }

        return builder.ToString();
    }

    public static int Int(JsonElement args, string name, int fallback, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return fallback;

        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => fallback,
        };
        return Math.Clamp(parsed, min, max);
    }

    public static void WriteIso(Utf8JsonWriter writer, string name, DateTimeOffset when) =>
        writer.WriteString(name, when.ToString("O"));
}

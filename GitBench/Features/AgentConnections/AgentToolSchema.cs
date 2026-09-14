using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using McpSdk.Adapter.System.Text.Json;
using McpSdk.Protocol;
using McpSdk.Protocol.Models;
using RawJson = McpSdk.Protocol.Json;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// The wire shape of an exported tool: the assistant's schema literal as McpSdk's
/// <see cref="ObjectSchema"/>, carrying every property verbatim plus the bridge's own
/// <c>repo</c>; and the call arguments back the other way, as the <see cref="JsonElement"/> the
/// assistant tool reads, with <c>repo</c> taken off.
/// </summary>
/// <remarks>
/// McpSdk's typed property schemas keep only what they model — an array loses its item shape, a
/// string its enum — so each property is written from the literal's own JSON instead. The top
/// level is McpSdk's, which is how <c>required</c> round-trips and how the controller validates a
/// call before it reaches the tool; the literal's top-level <c>additionalProperties: false</c> is
/// dropped on purpose, since <c>repo</c> is one.
/// </remarks>
internal static class AgentToolSchema
{
    public const string RepoArgument = "repo";

    private const string RepoDescription =
        "Repository name or path; a path inside a worktree selects that worktree. Default: the "
        + "repo of the most recently active review window, else the main window's active repo.";

    public static Tool Describe(IAssistantTool tool)
    {
        using var literal = JsonDocument.Parse(tool.JsonSchema);
        var root = literal.RootElement;
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("required", out var requiredElement))
            foreach (var name in requiredElement.EnumerateArray())
                required.Add(name.GetString() ?? throw Malformed(tool, "a required entry is not a string"));

        var schema = new ObjectSchema();
        if (root.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name == RepoArgument)
                    throw Malformed(tool, $"it declares '{RepoArgument}', which the bridge adds");
                // Cloned: the document is disposed with this call, and the schema outlives it.
                var raw = new RawJsonSchema(property.Value.Clone());
                if (required.Contains(property.Name)) schema.Add(property.Name, raw);
                else schema.AddOption(property.Name, raw);
            }
        }
        schema.AddOption(RepoArgument, new StringSchema { Description = RepoDescription });

        return new Tool(tool.Name, tool.Description, schema)
        {
            Annotations = new ToolAnnotations { ReadOnlyHint = !tool.IsWrite, DestructiveHint = false },
        };
    }

    /// <summary>How a call's arguments split: the <c>repo</c> named, if any, and the rest.</summary>
    internal abstract record Arguments
    {
        public sealed record Split(string? Repo, JsonElement Rest) : Arguments;

        public sealed record Invalid(string Message) : Arguments;
    }

    /// <summary>Serializes once through the adapter and reads the result back: <c>repo</c> off
    /// the top, everything else as the element the assistant tool takes.</summary>
    public static Arguments SplitArguments(SystemJson json, IJsonObject arguments)
    {
        const string wrapper = "args";
        // The adapter writes an object only as a named member, so it is wrapped to be read back.
        using var document = JsonDocument.Parse(json.Stringify(writer => writer.Write(wrapper, arguments)));
        var root = document.RootElement.GetProperty(wrapper);

        string? repo = null;
        if (root.TryGetProperty(RepoArgument, out var repoElement) && repoElement.ValueKind != JsonValueKind.Null)
        {
            if (repoElement.ValueKind != JsonValueKind.String)
                return new Arguments.Invalid($"Argument '{RepoArgument}' must be a string (got {repoElement.GetRawText()}).");
            repo = repoElement.GetString();
        }

        return new Arguments.Split(repo, WithoutRepo(root));
    }

    private static JsonElement WithoutRepo(JsonElement args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in args.EnumerateObject())
                if (property.Name != RepoArgument)
                    property.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static InvalidOperationException Malformed(IAssistantTool tool, string why) =>
        new($"The schema of '{tool.Name}' cannot be exported: {why}.");

    /// <summary>A property schema written from the literal's JSON, member for member.</summary>
    private sealed class RawJsonSchema : JsonSchema
    {
        private readonly JsonElement _source;

        public RawJsonSchema(JsonElement source) => _source = source;

        public override void WriteMembers(IJsonWriter writer) => WriteObject(writer, _source);

        // The writer takes typed values, so a JSON schema's vocabulary is mapped kind by kind:
        // strings, numbers, booleans, nested objects, and arrays of strings (enum, required) or of
        // objects (items are one object, but a schema may list several).
        private static void WriteObject(IJsonWriter writer, JsonElement source)
        {
            foreach (var property in source.EnumerateObject())
            {
                var value = property.Value;
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        writer.Write(property.Name, value.GetString() ?? string.Empty);
                        break;
                    case JsonValueKind.Number:
                        if (value.TryGetInt32(out var whole)) writer.Write(property.Name, whole);
                        else writer.Write(property.Name, value.GetDouble());
                        break;
                    case JsonValueKind.True:
                        writer.Write(property.Name, true);
                        break;
                    case JsonValueKind.False:
                        writer.Write(property.Name, false);
                        break;
                    case JsonValueKind.Object:
                        writer.Write(property.Name, (RawJson)(nested => WriteObject(nested, value)));
                        break;
                    case JsonValueKind.Array:
                        WriteArray(writer, property.Name, value);
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                    default:
                        throw new InvalidOperationException(
                            $"Schema member '{property.Name}' is a {value.ValueKind}, which the export does not write.");
                }
            }
        }

        private static void WriteArray(IJsonWriter writer, string name, JsonElement array)
        {
            var items = new List<JsonElement>(array.GetArrayLength());
            foreach (var item in array.EnumerateArray()) items.Add(item);

            if (items.TrueForAll(item => item.ValueKind == JsonValueKind.String))
            {
                writer.Write(name, items.Select(item => item.GetString() ?? string.Empty).ToArray());
                return;
            }

            if (items.TrueForAll(item => item.ValueKind == JsonValueKind.Object))
            {
                writer.Write(name, items.Select(item => (RawJson)(nested => WriteObject(nested, item))).ToArray());
                return;
            }

            throw new InvalidOperationException(
                $"Schema member '{name}' is an array of mixed or unsupported kinds, which the export does not write.");
        }
    }
}

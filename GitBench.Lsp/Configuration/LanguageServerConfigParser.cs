using System.Text.Json;

namespace GitBench.Lsp.Configuration;

/// <summary>
/// Turns the hand-written config file into domain types. Everything downstream of this class is
/// trusted; nothing upstream of it is.
/// </summary>
/// <remarks>
/// The DOM is walked by hand rather than deserialized into a shape class, because a shape class
/// throws on the first field of the wrong type and takes the whole file with it. A file a person
/// edits by hand needs the opposite: one bad entry is skipped, and the rest still runs.
/// </remarks>
static class LanguageServerConfigParser
{
    public const int HighestSupportedVersion = 1;

    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ConfigParse Parse(string text)
    {
        // An empty file is a user who made the file and has not filled it in yet. That is "no
        // servers", not a broken config.
        if (string.IsNullOrWhiteSpace(text))
            return new ConfigParse.Loaded(LanguageServerConfig.Empty, []);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            return new ConfigParse.NotUnderstood(new ConfigError(
                ex.Message,
                ex.LineNumber is { } line ? (int)line + 1 : null,
                ex.BytePositionInLine is { } column ? (int)column + 1 : null));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ConfigParse.NotUnderstood(new ConfigError(
                    $"The config file must be a JSON object, but it is a {Describe(root.ValueKind)}."));

            var problems = new List<ConfigProblem>();

            if (Version(root, problems) is { } version && version > HighestSupportedVersion)
                return new ConfigParse.Unsupported(version, HighestSupportedVersion);

            var disabled = new List<LanguageId>();
            var servers = ReadServers(root, problems, disabled);
            var max = ReadMaxConcurrent(root, problems);
            return new ConfigParse.Loaded(
                new LanguageServerConfig(servers, max) { Disabled = disabled },
                problems);
        }
    }

    static int? Version(JsonElement root, List<ConfigProblem> problems)
    {
        if (!root.TryGetProperty("version", out var element)) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var version)) return version;

        problems.Add(new ConfigProblem("version", "Expected a whole number; read the file as the current schema."));
        return null;
    }

    static int ReadMaxConcurrent(JsonElement root, List<ConfigProblem> problems)
    {
        if (!root.TryGetProperty("maxConcurrentServers", out var element))
            return LanguageServerConfig.DefaultMaxConcurrentServers;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var max) && max > 0)
            return max;

        problems.Add(new ConfigProblem(
            "maxConcurrentServers",
            $"Expected a number above zero; using {LanguageServerConfig.DefaultMaxConcurrentServers}."));
        return LanguageServerConfig.DefaultMaxConcurrentServers;
    }

    static List<LanguageServerEntry> ReadServers(
        JsonElement root, List<ConfigProblem> problems, List<LanguageId> disabled)
    {
        var entries = new List<LanguageServerEntry>();
        if (!root.TryGetProperty("servers", out var servers))
            return entries;

        if (servers.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new ConfigProblem("servers", $"Expected an object, found a {Describe(servers.ValueKind)}."));
            return entries;
        }

        var claimed = new Dictionary<FileExtension, LanguageId>();
        var seen = new HashSet<LanguageId>();

        foreach (var property in servers.EnumerateObject())
        {
            if (!LanguageId.TryParse(property.Name, out var language))
            {
                problems.Add(new ConfigProblem(property.Name, "A server needs a language name."));
                continue;
            }

            if (!seen.Add(language))
            {
                problems.Add(new ConfigProblem(property.Name, "This language is configured more than once; kept the first."));
                continue;
            }

            if (ReadEntry(language, property.Value, problems, disabled) is not { } entry) continue;

            var extensions = new List<FileExtension>();
            foreach (var extension in entry.Extensions)
            {
                if (claimed.TryGetValue(extension, out var owner))
                {
                    problems.Add(new ConfigProblem(
                        language.Value,
                        $"'{extension}' is already handled by '{owner}', which is declared first."));
                    continue;
                }
                claimed[extension] = language;
                extensions.Add(extension);
            }

            if (extensions.Count == 0)
            {
                problems.Add(new ConfigProblem(language.Value, "Every file extension it claimed is handled by another server."));
                continue;
            }

            entries.Add(entry with { Extensions = extensions });
        }

        return entries;
    }

    // An entry is taken whole or not at all: a field of the wrong type means we do not know what
    // the user meant, and guessing launches a process on a guess.
    static LanguageServerEntry? ReadEntry(
        LanguageId language, JsonElement element, List<ConfigProblem> problems, List<LanguageId> disabled)
    {
        void Skip(string reason) => problems.Add(new ConfigProblem(language.Value, reason));

        if (element.ValueKind != JsonValueKind.Object)
        {
            Skip($"Expected an object, found a {Describe(element.ValueKind)}.");
            return null;
        }

        switch (Bool(element, "enabled", defaultValue: true))
        {
            case null:
                Skip("'enabled' must be true or false.");
                return null;
            case false:
                disabled.Add(language);
                return null;
        }

        if (NonEmptyString(element, "command") is not { } command)
        {
            Skip("'command' must name the program to run.");
            return null;
        }

        if (Strings(element, "args") is not { } args)
        {
            Skip("'args' must be a list of strings.");
            return null;
        }

        if (Extensions(element, out var extensions) is false)
        {
            Skip("'extensions' must be a list of file extensions such as \".rs\".");
            return null;
        }

        if (extensions.Count == 0)
        {
            Skip("'extensions' is required: a server that claims no file can never be started.");
            return null;
        }

        if (Strings(element, "rootMarkers") is not { } markers)
        {
            Skip("'rootMarkers' must be a list of file names.");
            return null;
        }

        if (StringMap(element, "env") is not { } environment)
        {
            Skip("'env' must be an object of string values.");
            return null;
        }

        if (RawObject(element, "initializationOptions", out var initialization) is false)
        {
            Skip("'initializationOptions' must be an object.");
            return null;
        }

        if (RawObject(element, "settings", out var settings) is false)
        {
            Skip("'settings' must be an object.");
            return null;
        }

        if (Milliseconds(element, "requestTimeoutMs", TimeSpan.FromSeconds(5)) is not { } requestTimeout)
        {
            Skip("'requestTimeoutMs' must be a number of milliseconds above zero.");
            return null;
        }

        if (Milliseconds(element, "idleShutdownMs", TimeSpan.FromMinutes(5)) is not { } idleShutdown)
        {
            Skip("'idleShutdownMs' must be a number of milliseconds above zero.");
            return null;
        }

        return new LanguageServerEntry(
            language, command, args, extensions, markers, environment,
            initialization, settings, requestTimeout, idleShutdown);
    }

    static bool? Bool(JsonElement parent, string name, bool defaultValue)
    {
        if (!parent.TryGetProperty(name, out var element)) return defaultValue;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    static string? NonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static IReadOnlyList<string>? Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array) return null;

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            values.Add(item.GetString()!);
        }
        return values;
    }

    static bool Extensions(JsonElement parent, out IReadOnlyList<FileExtension> extensions)
    {
        extensions = [];
        if (!parent.TryGetProperty("extensions", out var element)) return true;
        if (element.ValueKind != JsonValueKind.Array) return false;

        var parsed = new List<FileExtension>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            if (!FileExtension.TryParse(item.GetString(), out var extension)) return false;
            if (!parsed.Contains(extension)) parsed.Add(extension);
        }

        extensions = parsed;
        return true;
    }

    static IReadOnlyDictionary<string, string>? StringMap(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return new Dictionary<string, string>();
        if (element.ValueKind != JsonValueKind.Object) return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) return null;
            map[property.Name] = property.Value.GetString()!;
        }
        return map;
    }

    // Kept as text: these are handed back to the server verbatim, so the client never needs to
    // understand them and must not reshape them.
    static bool RawObject(JsonElement parent, string name, out string? json)
    {
        json = null;
        if (!parent.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Object) return false;

        json = element.GetRawText();
        return true;
    }

    static TimeSpan? Milliseconds(JsonElement parent, string name, TimeSpan defaultValue)
    {
        if (!parent.TryGetProperty(name, out var element)) return defaultValue;
        if (element.ValueKind != JsonValueKind.Number) return null;
        if (!element.TryGetInt32(out var value) || value <= 0) return null;
        return TimeSpan.FromMilliseconds(value);
    }

    static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "list",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "true/false",
        JsonValueKind.Null => "null",
        _ => "value",
    };
}

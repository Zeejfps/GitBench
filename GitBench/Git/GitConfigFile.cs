using System.Text;

namespace GitBench.Git;

// Reads `.git/config` instead of spawning `git config`. Local scope only, and include.path is not
// followed — a key reached only through an include reads as unset.
internal sealed class GitConfigFile
{
    private static readonly GitConfigFile EmptyConfig = new(new Dictionary<string, List<string>>(StringComparer.Ordinal));

    // "section\0subsection\0name" — remote names may contain dots, so a dotted key is ambiguous.
    private readonly Dictionary<string, List<string>> _values;

    private GitConfigFile(Dictionary<string, List<string>> values) => _values = values;

    public static GitConfigFile ForRepo(string repoPath)
        => Read(Path.Combine(CommonGitDir(Features.Repos.RepoGitDir.Resolve(repoPath)), "config"));

    public static GitConfigFile Read(string configPath)
    {
        if (!File.Exists(configPath)) return EmptyConfig;

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var reader = new StreamReader(configPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Parse(reader, values);
        return new GitConfigFile(values);
    }

    public string? Get(string section, string? subsection, string name)
        => _values.TryGetValue(Key(section, subsection, name), out var found) && found.Count > 0
            ? found[^1]
            : null;

    public IReadOnlyList<string> Subsections(string section)
    {
        var prefix = section.ToLowerInvariant() + "\0";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in _values.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var end = key.IndexOf('\0', prefix.Length);
            if (end > prefix.Length) found.Add(key[prefix.Length..end]);
        }
        return [.. found];
    }

    private static string CommonGitDir(string gitDir)
    {
        var pointer = Path.Combine(gitDir, "commondir");
        if (!File.Exists(pointer)) return gitDir;
        try
        {
            var target = File.ReadAllText(pointer).Trim();
            return target.Length == 0 ? gitDir : Path.GetFullPath(target, gitDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return gitDir;
        }
    }

    private static string Key(string section, string? subsection, string name)
        => $"{section.ToLowerInvariant()}\0{subsection ?? string.Empty}\0{name.ToLowerInvariant()}";

    private static void Parse(TextReader reader, Dictionary<string, List<string>> into)
    {
        var section = string.Empty;
        string? subsection = null;

        while (reader.ReadLine() is { } raw)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#' || line[0] == ';') continue;

            if (line[0] == '[')
            {
                ParseHeader(line, ref section, ref subsection);
                continue;
            }
            if (section.Length == 0) continue; // a key before any section header is malformed

            var eq = line.IndexOf('=');
            var name = (eq < 0 ? line : line[..eq]).Trim();
            if (name.IsEmpty) continue;

            // A bare key is boolean true, which is how `[extensions] worktreeConfig` is written.
            var value = eq < 0 ? "true" : ReadValue(line[(eq + 1)..], reader);

            var key = Key(section.ToString(), subsection, name.ToString());
            if (!into.TryGetValue(key, out var list)) into[key] = list = [];
            list.Add(value);
        }
    }

    private static void ParseHeader(ReadOnlySpan<char> line, ref string section, ref string? subsection)
    {
        var close = line.LastIndexOf(']');
        if (close < 0) return;
        var body = line[1..close].Trim();

        var quote = body.IndexOf('"');
        if (quote < 0)
        {
            // The deprecated [section.subsection] form, whose subsection IS case-insensitive.
            var dot = body.IndexOf('.');
            section = (dot < 0 ? body : body[..dot]).ToString();
            subsection = dot < 0 ? null : body[(dot + 1)..].ToString().ToLowerInvariant();
            return;
        }

        section = body[..quote].Trim().ToString();
        var rest = body[(quote + 1)..];
        var end = rest.LastIndexOf('"');
        subsection = UnescapeSubsection(end < 0 ? rest : rest[..end]);
    }

    private static string UnescapeSubsection(ReadOnlySpan<char> raw)
    {
        if (raw.IndexOf('\\') < 0) return raw.ToString();

        var name = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length) i++;
            name.Append(raw[i]);
        }
        return name.ToString();
    }

    private static string ReadValue(ReadOnlySpan<char> rest, TextReader reader)
    {
        var value = new StringBuilder();
        var quoted = false;
        var continued = true;
        // Pending unquoted whitespace: dropped if the value ends here, kept if anything follows —
        // including on the next line, since git counts the space before a continuation backslash
        // and the indent after it as two characters of the same value.
        var trailing = 0;

        while (continued)
        {
            continued = false;

            for (var i = 0; i < rest.Length; i++)
            {
                var c = rest[i];
                if (c == '"') { quoted = !quoted; continue; }

                if (c == '\\')
                {
                    if (i + 1 == rest.Length) { continued = true; break; } // line continuation
                    i++;
                    if (trailing > 0)
                    {
                        if (value.Length > 0) value.Append(' ', trailing);
                        trailing = 0;
                    }
                    value.Append(rest[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'b' => '\b',
                        var other => other, // covers \\ and \" and git's own "keep it" fallback
                    });
                    continue;
                }

                if (!quoted)
                {
                    if (c == '#' || c == ';') break;
                    if (char.IsWhiteSpace(c)) { trailing++; continue; }
                }

                if (trailing > 0)
                {
                    if (value.Length > 0) value.Append(' ', trailing);
                    trailing = 0;
                }
                value.Append(c);
            }

            if (continued && reader.ReadLine() is { } next) rest = next.AsSpan();
            else continued = false;
        }

        return value.ToString();
    }
}

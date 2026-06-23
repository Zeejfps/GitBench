using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GitBench.Localization.Generator;

[Generator]
public sealed class LocalizationGenerator : IIncrementalGenerator
{
    private const string ReferenceStem = "en";

    private enum Kind { Plain, Param, Plural }

    private static readonly string[] PluralCategories = { "other", "zero", "one", "two", "few", "many" };

    private static readonly DiagnosticDescriptor ParseError = new(
        id: "LOC001",
        title: "Localization catalog parse error",
        messageFormat: "Failed to parse '{0}': {1}",
        category: "Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MemberCollision = new(
        id: "LOC002",
        title: "Localization key produces a conflicting member",
        messageFormat: "Localization key '{0}' generates member '{1}', which {2}",
        category: "Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor LocaleWithoutCatalog = new(
        id: "LOC003",
        title: "Locale has no generated catalog",
        messageFormat: "Locale '{0}' has no generated catalog; add its string file or remove the enum case",
        category: "Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingTranslation = new(
        id: "LOC004",
        title: "Locale is missing a translation",
        messageFormat: "Locale '{0}' is missing a translation for key '{1}'",
        category: "Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnexpectedKey = new(
        id: "LOC005",
        title: "Locale defines an unknown key",
        messageFormat: "Locale '{0}' defines key '{1}' that is not in the reference catalog (en.json)",
        category: "Localization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ShapeMismatch = new(
        id: "LOC006",
        title: "Locale entry shape differs from the reference",
        messageFormat: "Locale '{0}' key '{1}' is {2} but the reference (en.json) is {3}; falling back to English",
        category: "Localization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static t =>
            {
                var p = Normalize(t.Path);
                return p.Contains("/localization/strings/") && p.EndsWith(".json", StringComparison.Ordinal);
            })
            .Select(static (t, ct) => (
                Path: t.Path,
                Stem: LocaleStem(t.Path).ToLowerInvariant(),
                Text: t.GetText(ct)?.ToString()))
            .Collect();

        var localeCases = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is EnumDeclarationSyntax { Identifier.ValueText: "Locale" },
                static (ctx, _) => ((EnumDeclarationSyntax)ctx.Node).Members
                    .Select(static m => m.Identifier.ValueText)
                    .ToImmutableArray())
            .Collect();

        context.RegisterSourceOutput(files.Combine(localeCases),
            static (spc, pair) => Generate(spc, pair.Left, pair.Right));
    }

    private static void Generate(
        SourceProductionContext spc,
        ImmutableArray<(string Path, string Stem, string? Text)> files,
        ImmutableArray<ImmutableArray<string>> declaredCases)
    {
        var locales = new List<(string Stem, string Name, List<Entry> Entries)>();
        foreach (var f in files.OrderBy(static f => f.Stem, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(f.Text))
                continue;

            try
            {
                locales.Add((f.Stem, Identifier(f.Stem), MiniJson.Parse(f.Text!)));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, f.Path, ex.Message));
            }
        }

        var reference = locales.FirstOrDefault(static l => l.Stem == ReferenceStem);
        if (reference.Entries is null)
            return;

        var withCatalog = new HashSet<string>(locales.Select(static l => l.Name)) { "Pseudo" };
        foreach (var declared in declaredCases.SelectMany(static c => c).Distinct())
            if (!withCatalog.Contains(declared))
                spc.ReportDiagnostic(Diagnostic.Create(LocaleWithoutCatalog, Location.None, declared));

        if (!ValidateReference(spc, locales, reference.Entries))
            return;

        var referenceKeys = reference.Entries.Select(static e => e.Key).ToList();
        var referenceSet = new HashSet<string>(referenceKeys);
        var referenceByKey = reference.Entries.ToDictionary(static e => e.Key);

        foreach (var locale in locales)
        {
            if (locale.Stem == ReferenceStem)
                continue;

            var byKey = ToLookup(locale.Entries);
            foreach (var key in referenceKeys)
            {
                if (!byKey.TryGetValue(key, out var translated))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(MissingTranslation, Location.None, locale.Stem, key));
                    continue;
                }

                if (translated.IsPlural != referenceByKey[key].IsPlural)
                    spc.ReportDiagnostic(Diagnostic.Create(ShapeMismatch, Location.None,
                        locale.Stem, key,
                        translated.IsPlural ? "plural" : "a flat string",
                        referenceByKey[key].IsPlural ? "plural" : "a flat string"));
            }

            foreach (var entry in locale.Entries)
                if (!referenceSet.Contains(entry.Key))
                    spc.ReportDiagnostic(Diagnostic.Create(UnexpectedKey, Location.None, locale.Stem, entry.Key));
        }

        spc.AddSource("Strings.g.cs",
            SourceText.From(Emit(locales, referenceByKey, referenceKeys), Encoding.UTF8));
    }

    private static bool ValidateReference(
        SourceProductionContext spc,
        List<(string Stem, string Name, List<Entry> Entries)> locales,
        List<Entry> reference)
    {
        var ok = true;
        var reserved = new HashSet<string>(locales.Select(static l => l.Name)) { "Pseudo", "For", "Culture" };
        var byIdentifier = new Dictionary<string, string>();

        foreach (var e in reference)
        {
            var id = Identifier(e.Key);

            if (reserved.Contains(id))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    MemberCollision, Location.None, e.Key, id, "conflicts with a built-in catalog member"));
                ok = false;
                continue;
            }

            if (byIdentifier.TryGetValue(id, out var firstKey))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    MemberCollision, Location.None, e.Key, id, $"conflicts with key '{firstKey}'"));
                ok = false;
                continue;
            }

            byIdentifier[id] = e.Key;
        }

        return ok;
    }

    private static string Emit(
        List<(string Stem, string Name, List<Entry> Entries)> locales,
        Dictionary<string, Entry> referenceByKey,
        List<string> referenceKeys)
    {
        var kinds = new Dictionary<string, (Kind Kind, List<string> Params)>();
        foreach (var key in referenceKeys)
            kinds[key] = Classify(referenceByKey[key]);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace GitBench.Localization;");
        sb.AppendLine();
        sb.AppendLine("public sealed partial class Strings");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly System.Globalization.CultureInfo _culture;");
        sb.AppendLine("    public System.Globalization.CultureInfo Culture => _culture;");
        sb.AppendLine();

        foreach (var key in referenceKeys)
        {
            var id = Identifier(key);
            var (kind, pnames) = kinds[key];
            switch (kind)
            {
                case Kind.Plain:
                    sb.AppendLine($"    public string {id} {{ get; }}");
                    break;
                case Kind.Param:
                    sb.AppendLine($"    private readonly string {Field(id)};");
                    sb.AppendLine($"    public string {id}({string.Join(", ", pnames.Select(p => $"object {p}"))}) => " +
                                  $"string.Format(_culture, {Field(id)}, {string.Join(", ", pnames)});");
                    break;
                case Kind.Plural:
                    sb.AppendLine($"    private readonly PluralForms {Field(id)};");
                    sb.AppendLine($"    public string {id}(int count) => " +
                                  $"string.Format(_culture, PluralRules.Select(_culture, {Field(id)}, count), count);");
                    break;
            }
        }

        sb.AppendLine();
        sb.Append("    private Strings(System.Globalization.CultureInfo culture");
        for (var i = 0; i < referenceKeys.Count; i++)
        {
            var type = kinds[referenceKeys[i]].Kind == Kind.Plural ? "PluralForms" : "string";
            sb.Append($", {type} p{i}");
        }
        sb.AppendLine(")");
        sb.AppendLine("    {");
        sb.AppendLine("        _culture = culture;");
        for (var i = 0; i < referenceKeys.Count; i++)
        {
            var id = Identifier(referenceKeys[i]);
            var target = kinds[referenceKeys[i]].Kind == Kind.Plain ? id : Field(id);
            sb.AppendLine($"        {target} = p{i};");
        }
        sb.AppendLine("    }");

        foreach (var locale in locales)
        {
            var byKey = ToLookup(locale.Entries);
            EmitInstance(sb, locale.Name, locale.Stem, referenceKeys, kinds, referenceByKey, byKey, pseudo: false);
        }

        EmitInstance(sb, "Pseudo", ReferenceStem, referenceKeys, kinds, referenceByKey, null, pseudo: true);

        sb.AppendLine();
        sb.AppendLine("    public static Strings For(Locale locale) => locale switch");
        sb.AppendLine("    {");
        foreach (var locale in locales)
            sb.AppendLine($"        Locale.{locale.Name} => {locale.Name},");
        sb.AppendLine("        Locale.Pseudo => Pseudo,");
        sb.AppendLine("        _ => throw new System.ArgumentOutOfRangeException(");
        sb.AppendLine("            nameof(locale), locale, \"No generated catalog for this locale.\"),");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitInstance(
        StringBuilder sb,
        string name,
        string cultureStem,
        List<string> keys,
        Dictionary<string, (Kind Kind, List<string> Params)> kinds,
        Dictionary<string, Entry> referenceByKey,
        Dictionary<string, Entry>? byKey,
        bool pseudo)
    {
        sb.AppendLine();
        sb.AppendLine($"    public static readonly Strings {name} = new(");
        sb.Append($"        System.Globalization.CultureInfo.GetCultureInfo(\"{cultureStem}\")");
        foreach (var key in keys)
        {
            var (kind, pnames) = kinds[key];
            var refEntry = referenceByKey[key];
            var locEntry = byKey != null && byKey.TryGetValue(key, out var le) ? le : null;
            if (locEntry != null && locEntry.IsPlural != refEntry.IsPlural)
                locEntry = null;
            sb.Append(",\n        " + ValueExpr(kind, pnames, refEntry, locEntry, pseudo));
        }

        sb.AppendLine(");");
    }

    private static (Kind, List<string>) Classify(Entry reference)
    {
        if (reference.IsPlural)
            return (Kind.Plural, new List<string> { "count" });

        var pnames = ExtractParams(reference.Text ?? "");
        return pnames.Count == 0 ? (Kind.Plain, pnames) : (Kind.Param, pnames);
    }

    private static string ValueExpr(Kind kind, List<string> pnames, Entry refEntry, Entry? locEntry, bool pseudo)
    {
        switch (kind)
        {
            case Kind.Plain:
            {
                var text = pseudo ? Pseudoize(refEntry.Text ?? "") : (locEntry?.Text ?? refEntry.Text ?? "");
                return $"\"{Escape(text)}\"";
            }
            case Kind.Param:
            {
                var raw = locEntry?.Text ?? refEntry.Text ?? "";
                var pos = ToPositional(raw, pnames);
                if (pseudo) pos = Pseudoize(ToPositional(refEntry.Text ?? "", pnames));
                return $"\"{Escape(pos)}\"";
            }
            default:
            {
                var forms = locEntry?.Plural ?? refEntry.Plural!;
                var refForms = ToDict(refEntry.Plural!);
                var formDict = ToDict(forms);
                var count = new List<string> { "count" };

                var args = new List<string>();
                foreach (var cat in PluralCategories)
                {
                    var template = formDict.TryGetValue(cat, out var v) ? v
                        : refForms.TryGetValue(cat, out var rv) ? rv
                        : null;
                    if (template == null)
                    {
                        if (cat != "other") continue;
                        template = refForms.TryGetValue("other", out var ro) ? ro : "";
                    }

                    var pos = ToPositional(template, count);
                    if (pseudo)
                        pos = Pseudoize(ToPositional(
                            refForms.TryGetValue(cat, out var rp) ? rp : template, count));
                    args.Add($"{cat}: \"{Escape(pos)}\"");
                }

                return $"new PluralForms({string.Join(", ", args)})";
            }
        }
    }

    private static List<string> ExtractParams(string template)
    {
        var names = new List<string>();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var name = template.Substring(i + 1, close - i - 1);
                    if (IsIdentifier(name) && !names.Contains(name))
                        names.Add(name);
                    i = close + 1;
                    continue;
                }
            }

            i++;
        }

        return names;
    }

    private static string ToPositional(string template, List<string> pnames)
    {
        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var name = template.Substring(i + 1, close - i - 1);
                    var idx = pnames.IndexOf(name);
                    if (idx >= 0)
                    {
                        sb.Append('{').Append(idx).Append('}');
                        i = close + 1;
                        continue;
                    }
                }

                sb.Append("{{");
                i++;
                continue;
            }

            if (c == '}')
            {
                sb.Append("}}");
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || char.IsDigit(s[0]))
            return false;
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        return true;
    }

    private static Dictionary<string, string> ToDict(List<KeyValuePair<string, string>> entries)
    {
        var lookup = new Dictionary<string, string>();
        foreach (var e in entries)
            lookup[e.Key] = e.Value;
        return lookup;
    }

    private static Dictionary<string, Entry> ToLookup(List<Entry> entries)
    {
        var lookup = new Dictionary<string, Entry>();
        foreach (var e in entries)
            lookup[e.Key] = e;
        return lookup;
    }

    private static string Field(string identifier) =>
        "_" + char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private static string LocaleStem(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        var dot = name.IndexOf('.');
        return dot >= 0 ? name.Substring(0, dot) : name;
    }

    private static string Identifier(string key)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in key)
        {
            if (c is '.' or '_' or '-' or ' ' or '/')
            {
                upper = true;
                continue;
            }

            if (sb.Length == 0 && !char.IsLetter(c) && c != '_')
                sb.Append('_');

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    private static string Pseudoize(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('[');
        foreach (var c in s)
            sb.Append(Accent(c));

        var pad = Math.Max(1, s.Length / 3);
        sb.Append(' ');
        for (var i = 0; i < pad; i++)
            sb.Append('·');
        sb.Append(']');
        return sb.ToString();
    }

    private static char Accent(char c) => c switch
    {
        'a' => 'á', 'e' => 'é', 'i' => 'í', 'o' => 'ó', 'u' => 'ú',
        'A' => 'Á', 'E' => 'É', 'I' => 'Í', 'O' => 'Ó', 'U' => 'Ú',
        'c' => 'ç', 'C' => 'Ç', 'n' => 'ñ', 'N' => 'Ñ', 'y' => 'ý', 'Y' => 'Ý',
        _ => c,
    };
}

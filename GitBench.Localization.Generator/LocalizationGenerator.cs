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

        // The Locale enum is hand-authored (the System.Text.Json generator that serializes
        // Preferences references it and cannot see a generated enum). Read its cases so the catalog
        // can verify that every declared locale actually has values baked for it.
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
        // Parse every catalog file, reporting per-file parse errors but pressing on with the rest.
        var locales = new List<(string Stem, string Name, List<KeyValuePair<string, string>> Entries)>();
        foreach (var f in files.OrderBy(static f => f.Stem, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(f.Text))
                continue;

            try
            {
                locales.Add((f.Stem, Identifier(f.Stem), MiniJson.ParseFlatObject(f.Text!)));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, f.Path, ex.Message));
            }
        }

        var reference = locales.FirstOrDefault(static l => l.Stem == ReferenceStem);
        if (reference.Entries is null)
            return;

        // Every catalog the For() switch can return: one per parsed file, plus the derived Pseudo. Any
        // Locale enum case outside this set would hit For()'s throwing default at runtime, so flag it now.
        var withCatalog = new HashSet<string>(locales.Select(static l => l.Name)) { "Pseudo" };
        foreach (var declared in declaredCases.SelectMany(static c => c).Distinct())
            if (!withCatalog.Contains(declared))
                spc.ReportDiagnostic(Diagnostic.Create(LocaleWithoutCatalog, Location.None, declared));

        // en.json is the schema: it defines the key set, their order, and the member names. Bail on a
        // collision rather than emit members csc would reject with a cryptic "already defined".
        if (!ValidateReference(spc, locales, reference.Entries))
            return;

        var referenceKeys = reference.Entries.Select(static e => e.Key).ToList();
        var referenceSet = new HashSet<string>(referenceKeys);

        // Each translation must cover exactly the reference keys: missing one is an untranslated string
        // (build error); an extra one is a likely typo/stale key (warning).
        foreach (var locale in locales)
        {
            if (locale.Stem == ReferenceStem)
                continue;

            var keys = new HashSet<string>(locale.Entries.Select(static e => e.Key));
            foreach (var key in referenceKeys)
                if (!keys.Contains(key))
                    spc.ReportDiagnostic(Diagnostic.Create(MissingTranslation, Location.None, locale.Stem, key));
            foreach (var entry in locale.Entries)
                if (!referenceSet.Contains(entry.Key))
                    spc.ReportDiagnostic(Diagnostic.Create(UnexpectedKey, Location.None, locale.Stem, entry.Key));
        }

        spc.AddSource("Strings.g.cs", SourceText.From(Emit(locales, reference.Entries, referenceKeys), Encoding.UTF8));
    }

    // The catalog reserves the static instance names and For(); a key generating one of them would
    // shadow them with a cryptic "already defined" error from csc, so reject it with a clear diagnostic.
    private static bool ValidateReference(
        SourceProductionContext spc,
        List<(string Stem, string Name, List<KeyValuePair<string, string>> Entries)> locales,
        List<KeyValuePair<string, string>> reference)
    {
        var ok = true;
        var reserved = new HashSet<string>(locales.Select(static l => l.Name)) { "Pseudo", "For" };
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
        List<(string Stem, string Name, List<KeyValuePair<string, string>> Entries)> locales,
        List<KeyValuePair<string, string>> reference,
        List<string> referenceKeys)
    {
        var referenceValues = ToLookup(reference);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace GitBench.Localization;");
        sb.AppendLine();
        sb.AppendLine("public sealed partial class Strings");
        sb.AppendLine("{");

        foreach (var key in referenceKeys)
            sb.AppendLine($"    public required string {Identifier(key)} {{ get; init; }}");

        foreach (var locale in locales)
        {
            var values = ToLookup(locale.Entries);
            // Fall back to the English value for a key a translation is missing, so the instance still
            // compiles; the missing key has already been flagged by LOC004.
            EmitInstance(sb, locale.Name, referenceKeys,
                key => values.TryGetValue(key, out var v) ? v : referenceValues[key]);
        }

        EmitInstance(sb, "Pseudo", referenceKeys, key => Pseudoize(referenceValues[key]));

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

    private static void EmitInstance(StringBuilder sb, string name, List<string> keys, Func<string, string> value)
    {
        sb.AppendLine();
        sb.AppendLine($"    public static readonly Strings {name} = new()");
        sb.AppendLine("    {");
        foreach (var key in keys)
            sb.AppendLine($"        {Identifier(key)} = \"{Escape(value(key))}\",");
        sb.AppendLine("    };");
    }

    private static Dictionary<string, string> ToLookup(List<KeyValuePair<string, string>> entries)
    {
        var lookup = new Dictionary<string, string>();
        foreach (var e in entries)
            lookup[e.Key] = e.Value;
        return lookup;
    }

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

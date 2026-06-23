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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enFile = context.AdditionalTextsProvider
            .Where(static t => Normalize(t.Path).EndsWith("/localization/strings/en.json", StringComparison.Ordinal))
            .Select(static (t, ct) => (t.Path, Text: t.GetText(ct)?.ToString()));

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

        context.RegisterSourceOutput(enFile.Combine(localeCases), static (spc, pair) =>
        {
            var (file, declaredCases) = pair;
            var localeName = Identifier(LocaleStem(file.Path));

            // Catalogs the generator can produce: the reference locale (from en.json) plus the
            // always-derived Pseudo. Any enum case outside this set would fall through For()'s
            // throwing default at runtime, so reject it at build time instead.
            var withCatalog = new HashSet<string> { localeName, "Pseudo" };
            foreach (var declared in declaredCases.SelectMany(static c => c).Distinct())
                if (!withCatalog.Contains(declared))
                    spc.ReportDiagnostic(Diagnostic.Create(LocaleWithoutCatalog, Location.None, declared));

            if (string.IsNullOrWhiteSpace(file.Text))
                return;

            List<KeyValuePair<string, string>> entries;
            try
            {
                entries = MiniJson.ParseFlatObject(file.Text!);
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, file.Path, ex.Message));
                return;
            }

            if (!Validate(spc, localeName, entries))
                return;

            spc.AddSource("Strings.g.cs", SourceText.From(Emit(localeName, entries), Encoding.UTF8));
        });
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

    // The catalog reserves these member names; a key that generates one of them would shadow the
    // static instances or the For() switch with a cryptic "already defined" error from csc, so reject
    // it up front with a diagnostic that names the offending key.
    private static IEnumerable<string> ReservedMembers(string localeName)
    {
        yield return localeName;
        yield return "Pseudo";
        yield return "For";
    }

    private static bool Validate(SourceProductionContext spc, string localeName, List<KeyValuePair<string, string>> entries)
    {
        var ok = true;
        var reserved = new HashSet<string>(ReservedMembers(localeName));
        var byIdentifier = new Dictionary<string, string>();

        foreach (var e in entries)
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

    private static string Emit(string localeName, List<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace GitBench.Localization;");
        sb.AppendLine();
        sb.AppendLine("public sealed partial class Strings");
        sb.AppendLine("{");

        foreach (var e in entries)
            sb.AppendLine($"    public required string {Identifier(e.Key)} {{ get; init; }}");

        EmitInstance(sb, localeName, entries, pseudo: false);
        EmitInstance(sb, "Pseudo", entries, pseudo: true);

        sb.AppendLine();
        sb.AppendLine("    public static Strings For(Locale locale) => locale switch");
        sb.AppendLine("    {");
        sb.AppendLine($"        Locale.{localeName} => {localeName},");
        sb.AppendLine("        Locale.Pseudo => Pseudo,");
        sb.AppendLine("        _ => throw new System.ArgumentOutOfRangeException(");
        sb.AppendLine("            nameof(locale), locale, \"No generated catalog for this locale.\"),");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitInstance(StringBuilder sb, string name, List<KeyValuePair<string, string>> entries, bool pseudo)
    {
        sb.AppendLine();
        sb.AppendLine($"    public static readonly Strings {name} = new()");
        sb.AppendLine("    {");
        foreach (var e in entries)
        {
            var value = pseudo ? Pseudoize(e.Value) : e.Value;
            sb.AppendLine($"        {Identifier(e.Key)} = \"{Escape(value)}\",");
        }
        sb.AppendLine("    };");
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

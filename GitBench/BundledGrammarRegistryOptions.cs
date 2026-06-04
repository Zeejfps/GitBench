using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace GitGui;

/// <summary>
/// Wraps TextMateSharp's default <see cref="RegistryOptions"/> and serves a few grammars VS Code
/// (and therefore the bundled grammar set) doesn't ship — currently just Svelte. Every other
/// scope, including the embedded languages a Svelte block pulls in (<c>source.ts</c>,
/// <c>source.css</c>, <c>source.js</c>, …), falls straight through to the inner options, so those
/// resolve from the bundled set exactly as before.
/// </summary>
/// <remarks>
/// Adding a language TextMateSharp doesn't bundle is two steps: drop its <c>.tmLanguage.json</c>
/// into <c>Assets/Grammars</c> (it gets embedded by the csproj) and add one row to
/// <see cref="Grammars"/>. Then register the extension in <see cref="LanguageRegistry"/>.
/// </remarks>
internal sealed class BundledGrammarRegistryOptions : IRegistryOptions
{
    // (language id, grammar scope, embedded resource name). The scope must equal the grammar
    // file's own "scopeName"; the resource name is the file's LogicalName (see GitBench.csproj).
    private static readonly (string LanguageId, string Scope, string Resource)[] Grammars =
    {
        ("svelte", "source.svelte", "svelte.tmLanguage.json"),
    };

    private readonly RegistryOptions _inner;

    public BundledGrammarRegistryOptions(RegistryOptions inner) => _inner = inner;

    // The TextMate scope a custom-grammar language tokenizes as, or null for anything the bundled
    // set already knows (the caller then falls back to RegistryOptions.GetScopeByLanguageId).
    public static string? ScopeForLanguageId(string languageId)
    {
        foreach (var g in Grammars)
            if (g.LanguageId == languageId) return g.Scope;
        return null;
    }

    public IRawGrammar GetGrammar(string scopeName)
    {
        foreach (var g in Grammars)
            if (g.Scope == scopeName) return LoadEmbedded(g.Resource);
        return _inner.GetGrammar(scopeName);
    }

    public IRawTheme GetTheme(string scopeName) => _inner.GetTheme(scopeName);
    public IRawTheme GetDefaultTheme() => _inner.GetDefaultTheme();
    public ICollection<string> GetInjections(string scopeName) => _inner.GetInjections(scopeName);

    private static IRawGrammar LoadEmbedded(string logicalName)
    {
        var asm = typeof(BundledGrammarRegistryOptions).Assembly;
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded grammar '{logicalName}' is missing.");
        using var reader = new StreamReader(stream);
        return GrammarReader.ReadGrammarSync(reader);
    }
}

namespace GitBench.Features.Diff;

/// <summary>
/// Maps a file path to the TextMate language id GitBench highlights it as, or null when the
/// language isn't supported (the diff then renders plain). This is the single place new
/// languages are registered: add one extension → language-id row. The returned id is the one
/// TextMateSharp's <c>RegistryOptions.GetScopeByLanguageId</c> understands.
/// </summary>
internal static class LanguageRegistry
{
    // Extension (with leading dot) → TextMateSharp language id. Case-insensitive: paths from
    // git keep their on-disk casing, and ".CS" must highlight the same as ".cs".
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescriptreact",
        [".json"] = "json",
        // MSBuild project files are XML; the bundled xml grammar already claims these extensions.
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".xml"] = "xml",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".md"] = "markdown",
        [".markdown"] = "markdown",
        // Svelte isn't a TextMateSharp-bundled grammar; BundledGrammarRegistryOptions ships it.
        [".svelte"] = "svelte",
    };

    public static string? DetectLanguageId(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        return ByExtension.TryGetValue(ext, out var id) ? id : null;
    }
}

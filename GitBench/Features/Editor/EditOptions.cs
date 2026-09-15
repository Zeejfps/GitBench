using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Infrastructure;

namespace GitBench.Features.Editor;

/// <summary>What a press of Tab actually puts in the file, as distinct from how wide a tab is drawn.</summary>
internal enum IndentStyle
{
    Tabs,
    Spaces,
}

/// <summary>The per-document typing settings: what an indent is made of, what ends a line, and what
/// starts a comment.</summary>
internal sealed class EditOptions
{
    /// <summary>What the app assumes about a file it has been told nothing about.</summary>
    public static readonly EditOptions Default = new(IndentStyle.Spaces, LineEnding.Lf, null);

    public EditOptions(IndentStyle indent, LineEnding eol, string? lineComment)
    {
        if (lineComment is { Length: 0 })
            throw new ArgumentException("A line comment token is absent or it is text; it is never empty.", nameof(lineComment));

        Indent = indent;
        Eol = eol;
        LineComment = lineComment;
    }

    public IndentStyle Indent { get; }

    public LineEnding Eol { get; }

    /// <summary>What starts a comment that runs to the end of the line, or null for a language that
    /// has none.</summary>
    public string? LineComment { get; }

    public string EolText => Eol.Text();

    /// <summary>The text one indent is worth for a caret sitting at <paramref name="cell"/>: spaces
    /// run to the next tab stop rather than always a whole tab width of them.</summary>
    public string IndentAt(int cell) =>
        Indent == IndentStyle.Tabs
            ? "\t"
            : new string(' ', DiffOptions.TabWidth - Math.Max(cell, 0) % DiffOptions.TabWidth);

    /// <summary>The settings a file is typed through, read off the file's own name and lines.</summary>
    public static EditOptions For(string path, LineEnding eol, IReadOnlyList<string> lines) =>
        new(IndentOf(lines), eol, LineCommentOf(path));

    private static IndentStyle IndentOf(IReadOnlyList<string> lines)
    {
        var tabs = 0;
        var spaces = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') tabs++;
            else if (line[0] == ' ') spaces++;
        }
        return tabs > spaces ? IndentStyle.Tabs : IndentStyle.Spaces;
    }

    private static string? LineCommentOf(string path)
    {
        var id = FileLanguage.Detect(path) switch
        {
            FileLanguage.TreeSitter(var language) => language.TextMateId(),
            FileLanguage.TextMate(var textMate) => textMate,
            FileLanguage.None => null,
            var other => throw new ArgumentOutOfRangeException(nameof(path), other, null),
        };
        return id is not null && LineComments.TryGetValue(id, out var token) ? token : null;
    }

    private static readonly Dictionary<string, string> LineComments = new()
    {
        ["bat"] = "REM",
        ["bibtex"] = "%",
        ["c"] = "//",
        ["clojure"] = ";",
        ["coffeescript"] = "#",
        ["cpp"] = "//",
        ["csharp"] = "//",
        ["cuda-cpp"] = "//",
        ["dart"] = "//",
        ["dockerfile"] = "#",
        ["fsharp"] = "//",
        ["go"] = "//",
        ["groovy"] = "//",
        ["hlsl"] = "//",
        ["ignore"] = "#",
        ["ini"] = ";",
        ["java"] = "//",
        ["javascript"] = "//",
        ["javascriptreact"] = "//",
        ["jsonc"] = "//",
        ["julia"] = "#",
        ["latex"] = "%",
        ["less"] = "//",
        ["lua"] = "--",
        ["makefile"] = "#",
        ["objective-c"] = "//",
        ["objective-cpp"] = "//",
        ["pascal"] = "//",
        ["perl"] = "#",
        ["perl6"] = "#",
        ["php"] = "//",
        ["powershell"] = "#",
        ["properties"] = "#",
        ["python"] = "#",
        ["r"] = "#",
        ["ruby"] = "#",
        ["rust"] = "//",
        ["scss"] = "//",
        ["shaderlab"] = "//",
        ["shellscript"] = "#",
        ["sql"] = "--",
        ["swift"] = "//",
        ["tex"] = "%",
        ["toml"] = "#",
        ["typescript"] = "//",
        ["typescriptreact"] = "//",
        ["typst"] = "//",
        ["typst-code"] = "//",
        ["vb"] = "'",
        ["yaml"] = "#",
    };
}

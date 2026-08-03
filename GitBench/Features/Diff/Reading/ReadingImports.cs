using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// Which rows of a hunk are import scaffolding, per language.
/// </summary>
/// <remarks>
/// Imports are hidden mechanically rather than by request. A reader almost never needs them, and
/// leaving the decision to the model spends its attention — and its coordinate budget — on the
/// least interesting rows in the diff. Classification runs once per side, so a context row inside
/// an import block is only hidden when both sides agree it is one.
/// </remarks>
internal static class ReadingImports
{
    private enum Block { None, GoGroup, ParenList, BraceUse }

    public static bool[] Classify(IReadOnlyList<DiffLine> lines, string? languageId)
    {
        var family = Family(languageId);
        var marked = new bool[lines.Count];
        if (family is null) return marked;

        var counts = new int[lines.Count];
        var sides = new int[lines.Count];

        MarkSide(lines, family, DiffLineKind.Added, counts, sides);
        MarkSide(lines, family, DiffLineKind.Removed, counts, sides);

        for (var i = 0; i < lines.Count; i++)
            marked[i] = sides[i] > 0 && counts[i] == sides[i];
        return marked;
    }

    private static void MarkSide(
        IReadOnlyList<DiffLine> lines,
        string family,
        DiffLineKind side,
        int[] counts,
        int[] sides)
    {
        var indices = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Kind == side || lines[i].Kind == DiffLineKind.Context)
                indices.Add(i);

        var hit = new bool[indices.Count];
        var block = Block.None;
        for (var n = 0; n < indices.Count; n++)
        {
            var text = lines[indices[n]].Text.Trim();
            if (block != Block.None)
            {
                hit[n] = true;
                if (Closes(block, text)) block = Block.None;
                continue;
            }
            if (Opens(family, text) is Block opened and not Block.None)
            {
                hit[n] = true;
                block = opened;
                continue;
            }
            hit[n] = IsImport(family, text);
        }

        BridgeBlankRuns(lines, indices, hit);

        for (var n = 0; n < indices.Count; n++)
        {
            sides[indices[n]]++;
            if (hit[n]) counts[indices[n]]++;
        }
    }

    // A blank row between two import rows belongs to the group; a blank row anywhere else does not.
    private static void BridgeBlankRuns(IReadOnlyList<DiffLine> lines, List<int> indices, bool[] hit)
    {
        var n = 0;
        while (n < indices.Count)
        {
            if (hit[n] || lines[indices[n]].Text.Trim().Length != 0)
            {
                n++;
                continue;
            }
            var end = n;
            while (end < indices.Count && !hit[end] && lines[indices[end]].Text.Trim().Length == 0)
                end++;
            var before = n > 0 && hit[n - 1];
            var after = end < indices.Count && hit[end];
            if (before && after)
                for (var i = n; i < end; i++) hit[i] = true;
            n = end;
        }
    }

    private static Block Opens(string family, string text) => family switch
    {
        "go" => text is "import (" or "import(" ? Block.GoGroup : Block.None,
        "python" => text.StartsWith("from ", StringComparison.Ordinal)
                    && text.Contains(" import ", StringComparison.Ordinal)
                    && text.EndsWith('(')
            ? Block.ParenList
            : Block.None,
        "rust" => text.StartsWith("use ", StringComparison.Ordinal) && text.EndsWith('{')
            ? Block.BraceUse
            : Block.None,
        "js" => (text.StartsWith("import ", StringComparison.Ordinal)
                 || text.StartsWith("export ", StringComparison.Ordinal))
                && text.EndsWith('{')
            ? Block.BraceUse
            : Block.None,
        _ => Block.None,
    };

    private static bool Closes(Block block, string text) => block switch
    {
        Block.GoGroup => text == ")",
        Block.ParenList => text.StartsWith(')'),
        Block.BraceUse => text.StartsWith('}'),
        _ => true,
    };

    private static bool IsImport(string family, string text)
    {
        if (text.Length == 0) return false;
        return family switch
        {
            "csharp" => IsCSharpUsing(text),
            "go" => IsGoImport(text),
            "python" => text.StartsWith("import ", StringComparison.Ordinal)
                        || (text.StartsWith("from ", StringComparison.Ordinal)
                            && text.Contains(" import ", StringComparison.Ordinal)),
            "js" => IsJsImport(text),
            "rust" => (text.StartsWith("use ", StringComparison.Ordinal)
                       || text.StartsWith("pub use ", StringComparison.Ordinal)
                       || text.StartsWith("extern crate ", StringComparison.Ordinal))
                      && text.EndsWith(';'),
            "c" => text.StartsWith("#include", StringComparison.Ordinal)
                   || text.StartsWith("#import", StringComparison.Ordinal),
            "java" => text.StartsWith("import ", StringComparison.Ordinal),
            _ => false,
        };
    }

    // `using System;` and `global using X = Y;` are scaffolding. A using statement or declaration
    // is control flow that happens to share the keyword, and must survive.
    private static bool IsCSharpUsing(string text)
    {
        var rest = text.StartsWith("global using ", StringComparison.Ordinal) ? text[7..] : text;
        if (!rest.StartsWith("using ", StringComparison.Ordinal)) return false;
        if (!rest.EndsWith(';')) return false;
        var body = rest[6..^1];
        if (body.Contains('(') || body.Contains(')')) return false;
        if (body.StartsWith("var ", StringComparison.Ordinal)) return false;
        return body.Length > 0;
    }

    private static bool IsGoImport(string text)
    {
        if (text.StartsWith("import ", StringComparison.Ordinal)) return true;
        // Rows of a group are quoted paths, optionally aliased or dot/underscore imported.
        var quote = text.IndexOf('"');
        if (quote < 0 || !text.EndsWith('"')) return false;
        var prefix = text[..quote].Trim();
        return prefix.Length == 0 || prefix == "_" || prefix == "." || !prefix.Contains(' ');
    }

    private static bool IsJsImport(string text)
    {
        if (text.StartsWith("import ", StringComparison.Ordinal)) return true;
        if (text.StartsWith("export ", StringComparison.Ordinal)
            && text.Contains(" from ", StringComparison.Ordinal)) return true;
        return text.Contains("require(", StringComparison.Ordinal)
               && (text.StartsWith("const ", StringComparison.Ordinal)
                   || text.StartsWith("let ", StringComparison.Ordinal)
                   || text.StartsWith("var ", StringComparison.Ordinal));
    }

    private static string? Family(string? languageId) => languageId switch
    {
        "csharp" => "csharp",
        "go" => "go",
        "python" => "python",
        "javascript" or "javascriptreact" or "typescript" or "typescriptreact" or "svelte" => "js",
        "rust" => "rust",
        "c" or "cpp" or "cuda-cpp" or "objective-c" or "objective-cpp" => "c",
        "java" or "kotlin" or "groovy" => "java",
        _ => null,
    };
}

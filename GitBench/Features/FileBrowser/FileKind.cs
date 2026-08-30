namespace GitBench.Features.FileBrowser;

/// <summary>
/// The coarse kind of a file, used only to tint its icon in the browser tree. Deliberately five
/// buckets and not one per language: the tint exists so a directory of sources reads differently
/// from a directory of build output at a glance, and a hue per extension would be confetti.
/// </summary>
internal enum FileKind
{
    Other,
    Code,
    Data,
    Docs,
    Media,
    Binary,
}

/// <summary>
/// Maps a file name to its <see cref="FileKind"/>. Extension first, then a small set of
/// extensionless names that every repository has; a leading-dot name with no other dot is config
/// by convention. Allocation-free because it runs for every visible row of every frame.
/// </summary>
internal static class FileKinds
{
    public static FileKind Classify(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return ByName(name);

        var ext = name.AsSpan(dot + 1);
        Span<char> lower = stackalloc char[16];
        if (ext.Length > lower.Length) return FileKind.Other;
        var written = ext.ToLowerInvariant(lower);
        return ByExtension(lower[..written]);
    }

    private static FileKind ByExtension(ReadOnlySpan<char> ext) => ext switch
    {
        "cs" or "fs" or "fsx" or "vb" or "ts" or "tsx" or "js" or "jsx" or "mjs" or "cjs"
            or "py" or "rb" or "go" or "rs" or "java" or "kt" or "kts" or "swift" or "dart"
            or "c" or "h" or "cc" or "cpp" or "hpp" or "cxx" or "hxx" or "m" or "mm"
            or "php" or "lua" or "pl" or "r" or "scala" or "clj" or "ex" or "exs" or "erl"
            or "sh" or "bash" or "zsh" or "fish" or "ps1" or "psm1" or "bat" or "cmd"
            or "sql" or "gradle" or "cmake" or "mk" or "nix" or "zig"
            or "html" or "htm" or "css" or "scss" or "sass" or "less"
            or "vue" or "svelte" or "razor" or "cshtml" or "xaml" or "axaml" => FileKind.Code,

        "json" or "jsonc" or "json5" or "yaml" or "yml" or "toml" or "ini" or "cfg" or "conf"
            or "env" or "properties" or "csv" or "tsv" or "xml" or "plist" or "resx" or "lock"
            or "csproj" or "fsproj" or "vbproj" or "sln" or "slnx" or "props" or "targets"
            or "nuspec" or "editorconfig" or "gitignore" or "gitattributes" => FileKind.Data,

        "md" or "markdown" or "mdx" or "txt" or "rst" or "adoc" or "asciidoc" or "org"
            or "tex" or "pdf" or "doc" or "docx" or "odt" or "rtf" or "epub" => FileKind.Docs,

        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" or "ico" or "icns"
            or "tif" or "tiff" or "avif" or "heic" or "psd" or "ai"
            or "mp4" or "mov" or "avi" or "mkv" or "webm" or "m4v"
            or "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => FileKind.Media,

        "dll" or "exe" or "so" or "dylib" or "a" or "o" or "obj" or "lib" or "pdb" or "bin"
            or "dat" or "wasm" or "class" or "jar" or "nupkg" or "snupkg"
            or "zip" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "zst" or "7z" or "rar"
            or "dmg" or "pkg" or "iso" or "deb" or "rpm"
            or "ttf" or "otf" or "woff" or "woff2" or "eot" => FileKind.Binary,

        _ => FileKind.Other,
    };

    private static FileKind ByName(string name)
    {
        Span<char> lower = stackalloc char[24];
        var bare = name.AsSpan(name.StartsWith('.') ? 1 : 0);
        if (bare.Length > lower.Length) return FileKind.Other;
        var written = bare.ToLowerInvariant(lower);

        return lower[..written] switch
        {
            "readme" or "license" or "licence" or "copying" or "notice" or "changelog"
                or "changes" or "authors" or "contributors" or "contributing" => FileKind.Docs,

            "makefile" or "dockerfile" or "containerfile" or "rakefile" or "gemfile"
                or "procfile" or "justfile" or "brewfile" or "vagrantfile" => FileKind.Code,

            _ => name.StartsWith('.') ? FileKind.Data : FileKind.Other,
        };
    }
}

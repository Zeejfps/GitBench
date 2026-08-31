using System.Diagnostics;
using System.Text;

using TreeSitter;

namespace GitBench.Features.CodeIntel;

internal sealed class TreeSitterSymbolExtractor : ISymbolExtractor, IDisposable
{
    public const int MaxFileBytes = 1024 * 1024;

    private const string GrammarLibrary = "tree-sitter-grammars";
    private const string DefinitionCapturePrefix = "def.";
    private const string NameCapture = "name";
    private const string BodyCapture = "body";
    private const string ExtentCapture = "extent";

    private static readonly TimeSpan WholeFileBudget = TimeSpan.FromMilliseconds(750);

    private readonly Dictionary<CodeLanguage, CompiledLanguage>? _compiled;
    private readonly Action<string>? _log;
    private int _parseFailureLogged;
    private int _budgetLogged;

    public TreeSitterSymbolExtractor(
        Action<string>? log = null,
        int? poolCapacity = null,
        Func<CodeLanguage, string>? queryText = null)
    {
        _log = log;

        var capacity = poolCapacity ?? Environment.ProcessorCount;
        var read = queryText ?? ReadEmbeddedQuery;
        var compiled = new Dictionary<CodeLanguage, CompiledLanguage>();
        string? firstFailure = null;

        // Per language, not all-or-nothing. A grammar pin that renames a node breaks the query
        // written against it and nothing else, and with fifteen bundled languages one such break
        // taking code intelligence down for the other fourteen is a far worse failure than the one
        // it is reporting. The language that failed simply has no outline, which is a state every
        // caller already handles.
        foreach (var language in CodeLanguages.All)
        {
            try
            {
                compiled.Add(language, CompiledLanguage.Create(language, capacity, read(language)));
            }
            catch (Exception error)
            {
                firstFailure ??= error.Message;
                _log?.Invoke($"Code intelligence unavailable for {language}: {error}");
            }
        }

        if (compiled.Count > 0)
        {
            _compiled = compiled;
            Availability = CodeIntelAvailability.Ready.Instance;
            return;
        }

        _compiled = null;
        Availability = new CodeIntelAvailability.Unavailable(firstFailure ?? "No language loaded.");
    }

    public CodeIntelAvailability Availability { get; }

    /// <summary>Whether this language's grammar and query both loaded. Availability answers "is
    /// parsing possible at all"; with fifteen bundled languages one can be broken while the rest
    /// work, and a test that only asked the former would not notice.</summary>
    internal bool Supports(CodeLanguage language) => _compiled?.ContainsKey(language) == true;

    public FileOutline? Extract(string text, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_compiled is null || !_compiled.TryGetValue(language, out var compiled)) return null;
        if (text.Length > MaxFileBytes) return null;

        var normalized = NormalizeNewlines(text);
        if (Encoding.UTF8.GetByteCount(normalized) > MaxFileBytes) return null;
        var utf8 = Encoding.UTF8.GetBytes(normalized);

        var watch = Stopwatch.StartNew();
        FileOutline? outline;
        try
        {
            outline = compiled.Pool.Use((compiled, utf8), static (session, s) => Walk(session, s.compiled, s.utf8));
        }
        catch (Exception error)
        {
            LogOnce(ref _parseFailureLogged, $"Code intelligence failed to parse a {language} file: {error}");
            return null;
        }

        if (watch.Elapsed <= WholeFileBudget) return outline;

        LogOnce(
            ref _budgetLogged,
            $"Code intelligence exceeded its {WholeFileBudget.TotalMilliseconds:F0} ms budget " +
            $"on a {utf8.Length} byte {language} file ({watch.ElapsedMilliseconds} ms).");
        return null;
    }

    public void Dispose()
    {
        if (_compiled is null) return;
        foreach (var entry in _compiled.Values)
        {
            entry.Dispose();
        }
    }

    private void LogOnce(ref int flag, string message)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
        {
            _log?.Invoke(message);
        }
    }

    public static string ReadEmbeddedQuery(CodeLanguage language)
    {
        var resource = language.QueryResourceName();
        var assembly = typeof(TreeSitterSymbolExtractor).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded tree-sitter query '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizeNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private static FileOutline? Walk(ParseSession session, CompiledLanguage compiled, byte[] utf8)
    {
        using var tree = session.Parser.Parse(utf8);

        var found = new List<Pending>();
        var seen = new HashSet<(uint Start, uint End)>();

        session.Cursor.ForEachMatch(compiled.Query, tree.RootNode, match =>
        {
            if (!compiled.TryReadDefinition(match, out var definition, out var kind)) return;
            if (!match.TryGetNode(compiled.NameCaptureId, out var name)) return;
            if (!seen.Add((definition.StartByte, definition.EndByte))) return;

            var extent = compiled.HasExtentCapture && match.TryGetNode(compiled.ExtentCaptureId, out var extentNode)
                ? extentNode
                : definition;

            var startLine = StartLineOf(definition, compiled.LeadingDecorations);
            var endLine = (int)definition.EndPoint.Row + 1;
            var signatureEndLine = endLine;

            if (compiled.HasBodyCapture && match.TryGetNode(compiled.BodyCaptureId, out var body))
            {
                signatureEndLine = Math.Clamp((int)body.StartPoint.Row + 1, startLine, endLine);
            }

            found.Add(new Pending(
                definition.StartByte,
                extent.StartByte,
                extent.EndByte,
                name.Text,
                kind,
                ParameterTypesOf(definition),
                startLine,
                endLine,
                signatureEndLine));
        });

        if (found.Count == 0) return null;

        found.Sort(static (a, b) =>
        {
            var byStart = a.ExtentStartByte.CompareTo(b.ExtentStartByte);
            if (byStart != 0) return byStart;

            var byEnd = b.ExtentEndByte.CompareTo(a.ExtentEndByte);
            return byEnd != 0 ? byEnd : a.StartByte.CompareTo(b.StartByte);
        });

        var roots = new List<Draft>();
        var open = new Stack<Draft>();

        foreach (var pending in found)
        {
            while (open.Count > 0 && open.Peek().ExtentEndByte <= pending.StartByte)
            {
                open.Pop();
            }

            var draft = new Draft(pending);
            if (open.Count > 0) open.Peek().Children.Add(draft);
            else roots.Add(draft);

            open.Push(draft);
        }

        return new FileOutline(Freeze(roots));
    }

    private static IReadOnlyList<OutlineNode> Freeze(List<Draft> drafts)
    {
        if (drafts.Count == 0) return [];

        var nodes = new OutlineNode[drafts.Count];
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            nodes[i] = new OutlineNode(
                draft.Pending.Name,
                draft.Pending.Kind,
                draft.Pending.ParameterTypes,
                draft.Pending.StartLine,
                draft.Pending.EndLine,
                draft.Pending.SignatureEndLine,
                Freeze(draft.Children));
        }

        return nodes;
    }

    private static int StartLineOf(Node definition, IReadOnlyList<string> leadingDecorations)
    {
        if (leadingDecorations.Count == 0) return (int)definition.StartPoint.Row + 1;

        foreach (var child in definition.Children)
        {
            if (leadingDecorations.Contains(child.Type)) continue;
            return (int)child.StartPoint.Row + 1;
        }

        return (int)definition.StartPoint.Row + 1;
    }

    private static string? ParameterTypesOf(Node definition)
    {
        if (definition.ChildByFieldName("parameters") is not { } parameters) return null;

        var listName = parameters.ChildByFieldName("name");
        var builder = new StringBuilder();

        foreach (var child in parameters.NamedChildren)
        {
            if (listName is { } trailing && child.StartByte == trailing.StartByte && child.EndByte == trailing.EndByte)
            {
                continue;
            }

            var type = TypeOf(child);

            if (builder.Length > 0) builder.Append(", ");
            AppendCollapsed(builder, type.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The parameter's declared type, or the whole node where it does not name one — a lambda's
    /// untyped argument, or C#'s <c>params int[] rest</c>, which the grammar spills into the
    /// parameter list as a bare <c>array_type</c> that already <em>is</em> the type.
    /// </summary>
    /// <remarks>
    /// TypeScript writes the type as an annotation node carrying its own colon, so that one case is
    /// unwrapped rather than rendered as <c>": string"</c>.
    /// </remarks>
    private static Node TypeOf(Node parameter)
    {
        // Only a node that also binds a name or a pattern is parameter-shaped. C# spills
        // `params int[] rest` into the parameter list as a bare array_type, which has a type field
        // of its own and yet already *is* the type — reading that field would return `int`.
        var named = parameter.ChildByFieldName("name") ?? parameter.ChildByFieldName("pattern");
        if (named is null || parameter.ChildByFieldName("type") is not { } type) return parameter;

        // TypeScript writes the type as an annotation node carrying its own colon.
        if (type.Type != "type_annotation") return type;

        Node? inner = null;
        foreach (var child in type.NamedChildren) inner = child;
        return inner ?? type;
    }

    private static void AppendCollapsed(StringBuilder builder, string text)
    {
        var start = builder.Length;
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > start;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }
    }

    private readonly record struct Pending(
        uint StartByte,
        uint ExtentStartByte,
        uint ExtentEndByte,
        string Name,
        SymbolKind Kind,
        string? ParameterTypes,
        int StartLine,
        int EndLine,
        int SignatureEndLine);

    private sealed class Draft(Pending pending)
    {
        public Pending Pending { get; } = pending;

        public uint ExtentEndByte { get; } = pending.ExtentEndByte;

        public List<Draft> Children { get; } = [];
    }

    private sealed class CompiledLanguage : IDisposable
    {
        private readonly Dictionary<uint, SymbolKind> _definitionCaptures;

        private CompiledLanguage(
            Query query,
            ParseSessionPool pool,
            Dictionary<uint, SymbolKind> definitionCaptures,
            uint nameCaptureId,
            uint bodyCaptureId,
            bool hasBodyCapture,
            uint extentCaptureId,
            bool hasExtentCapture,
            IReadOnlyList<string> leadingDecorations)
        {
            Query = query;
            Pool = pool;
            _definitionCaptures = definitionCaptures;
            NameCaptureId = nameCaptureId;
            BodyCaptureId = bodyCaptureId;
            HasBodyCapture = hasBodyCapture;
            ExtentCaptureId = extentCaptureId;
            HasExtentCapture = hasExtentCapture;
            LeadingDecorations = leadingDecorations;
        }

        public Query Query { get; }

        public ParseSessionPool Pool { get; }

        public uint NameCaptureId { get; }

        public uint BodyCaptureId { get; }

        public bool HasBodyCapture { get; }

        public uint ExtentCaptureId { get; }

        public bool HasExtentCapture { get; }

        public IReadOnlyList<string> LeadingDecorations { get; }

        public static CompiledLanguage Create(CodeLanguage language, int poolCapacity, string queryText)
        {
            var grammar = Language.Load(GrammarLibrary, language.GrammarName());
            var query = Query.Compile(grammar, queryText);

            try
            {
                var definitionCaptures = new Dictionary<uint, SymbolKind>();
                uint nameCaptureId = 0;
                var hasNameCapture = false;
                uint bodyCaptureId = 0;
                var hasBodyCapture = false;
                uint extentCaptureId = 0;
                var hasExtentCapture = false;

                for (var id = 0u; id < query.CaptureCount; id++)
                {
                    var name = query.CaptureName(id);

                    if (name.StartsWith(DefinitionCapturePrefix, StringComparison.Ordinal))
                    {
                        var suffix = name.AsSpan(DefinitionCapturePrefix.Length);
                        if (!SymbolKinds.TryParseCaptureSuffix(suffix, out var kind))
                        {
                            throw new InvalidOperationException(
                                $"The {language} query captures '@{name}', but '{suffix}' is not a symbol kind. " +
                                $"Legal kinds: {string.Join(", ", SymbolKinds.CaptureSuffixes)}.");
                        }

                        definitionCaptures.Add(id, kind);
                    }
                    else if (name == NameCapture)
                    {
                        nameCaptureId = id;
                        hasNameCapture = true;
                    }
                    else if (name == BodyCapture)
                    {
                        bodyCaptureId = id;
                        hasBodyCapture = true;
                    }
                    else if (name == ExtentCapture)
                    {
                        extentCaptureId = id;
                        hasExtentCapture = true;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"The {language} query captures '@{name}', which is not part of the capture protocol. " +
                            $"Use '@{DefinitionCapturePrefix}<kind>', '@{NameCapture}', '@{BodyCapture}' " +
                            $"or '@{ExtentCapture}'.");
                    }
                }

                if (definitionCaptures.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The {language} query declares no '@{DefinitionCapturePrefix}<kind>' capture, " +
                        "so it can never produce an outline node.");
                }

                if (!hasNameCapture)
                {
                    throw new InvalidOperationException(
                        $"The {language} query declares no '@{NameCapture}' capture, " +
                        "so every match would be discarded.");
                }

                return new CompiledLanguage(
                    query,
                    new ParseSessionPool(grammar, poolCapacity),
                    definitionCaptures,
                    nameCaptureId,
                    bodyCaptureId,
                    hasBodyCapture,
                    extentCaptureId,
                    hasExtentCapture,
                    language.LeadingDecorationNodeTypes());
            }
            catch
            {
                query.Dispose();
                throw;
            }
        }

        public bool TryReadDefinition(QueryMatch match, out Node definition, out SymbolKind kind)
        {
            for (var i = 0; i < match.CaptureCount; i++)
            {
                if (_definitionCaptures.TryGetValue(match.CaptureIdAt(i), out kind))
                {
                    definition = match.NodeAt(i);
                    return true;
                }
            }

            definition = default;
            kind = default;
            return false;
        }

        public void Dispose()
        {
            Pool.Dispose();
            Query.Dispose();
        }
    }
}

using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>Whether a server offers parameter info, and which typed characters open it
/// (<see cref="Triggers"/>) or keep one already open current (<see cref="Retriggers"/>).</summary>
public abstract record SignatureHelpSupport
{
    private SignatureHelpSupport() { }

    public static readonly SignatureHelpSupport Unsupported = new None();

    public sealed record None : SignatureHelpSupport;

    public sealed record Offered(IReadOnlyList<char> Triggers, IReadOnlyList<char> Retriggers) : SignatureHelpSupport;

    internal static SignatureHelpSupport Read(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("signatureHelpProvider", out var provider)
            || provider.ValueKind is not (JsonValueKind.Object or JsonValueKind.True))
            return Unsupported;

        return provider.ValueKind == JsonValueKind.Object
            ? new Offered(Characters(provider, "triggerCharacters"), Characters(provider, "retriggerCharacters"))
            : new Offered([], []);
    }

    private static IReadOnlyList<char> Characters(JsonElement provider, string name)
    {
        var characters = new List<char>();
        if (provider.Optional(name) is not { ValueKind: JsonValueKind.Array } array) return characters;
        foreach (var character in array.EnumerateArray())
            if (character.ValueKind == JsonValueKind.String && character.GetString() is { Length: 1 } one)
                characters.Add(one[0]);
        return characters;
    }
}

/// <summary>Why parameter info was asked for.</summary>
public abstract record SignatureAsk
{
    private SignatureAsk() { }

    public static readonly SignatureAsk Invoked = new Requested();

    public static readonly SignatureAsk Following = new ContentChanged();

    public sealed record Requested : SignatureAsk;

    public sealed record TypedTrigger(char Character) : SignatureAsk;

    /// <summary>Asked again because the caret or the text moved while it was showing.</summary>
    public sealed record ContentChanged : SignatureAsk;
}

/// <summary>Where one parameter sits inside its signature's label, as a start and a length in the
/// label's own characters.</summary>
public readonly record struct ParameterSpan(int Start, int Length);

/// <summary>One overload: its whole label, where each parameter is inside it, and which one the
/// caret is in, where the server said so for this overload alone.</summary>
public sealed record SignatureInfo(
    string Label, string? Documentation, IReadOnlyList<ParameterSpan> Parameters, int? ActiveParameter);

/// <summary>
/// Every overload a call could be, which one the server thinks it is, and which parameter the caret
/// is in. Parameter labels are held as spans of the signature's label whichever of the protocol's
/// two shapes they arrived in — a substring or a pair of offsets — so nothing downstream has to know.
/// </summary>
public sealed record SignatureHelp(IReadOnlyList<SignatureInfo> Signatures, int ActiveSignature, int? ActiveParameter)
{
    public static readonly SignatureHelp Nothing = new([], 0, null);

    public static readonly ILspResultReader<SignatureHelp> Reader = new SignatureHelpReader();

    /// <summary>Whether the server said which parameter the caret is in, for the call or for any one
    /// overload. Some never do, and leave the client to count.</summary>
    public bool NamesActiveParameter => ActiveParameter is not null || Signatures.Any(s => s.ActiveParameter is not null);

    /// <summary>The parameter the caret is in on an overload: its own answer where it gave one, the
    /// call's otherwise, and the first where neither did — which the protocol says an omitted answer
    /// means, and which some servers rely on for the first argument.</summary>
    public int? ActiveParameterOf(int signature)
    {
        if (signature < 0 || signature >= Signatures.Count) return null;
        var info = Signatures[signature];
        return info.ActiveParameter ?? ActiveParameter ?? (info.Parameters.Count > 0 ? 0 : null);
    }

    private sealed class SignatureHelpReader : ILspResultReader<SignatureHelp>
    {
        public SignatureHelp Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Nothing;
            if (result.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"signature help must be an object or null, was {result.ValueKind}");

            var signatures = new List<SignatureInfo>();
            if (result.Optional("signatures") is { ValueKind: JsonValueKind.Array } array)
                foreach (var signature in array.EnumerateArray()) signatures.Add(ReadSignature(signature));
            if (signatures.Count == 0) return Nothing;

            var active = Count(result, "activeSignature") ?? 0;
            return new SignatureHelp(
                signatures, Math.Clamp(active, 0, signatures.Count - 1), Count(result, "activeParameter"));
        }

        private static SignatureInfo ReadSignature(JsonElement signature)
        {
            var label = signature.RequireString("label");
            var spans = new List<ParameterSpan>();
            var from = 0;
            if (signature.Optional("parameters") is { ValueKind: JsonValueKind.Array } parameters)
            {
                foreach (var parameter in parameters.EnumerateArray())
                {
                    var span = SpanOf(label, parameter.Require("label"), from);
                    spans.Add(span);
                    from = span.Start + span.Length;
                }
            }

            return new SignatureInfo(label, DocumentationOf(signature), spans, Count(signature, "activeParameter"));
        }

        // A substring is searched for after the parameter before it, so `(int a, int b)` finds the
        // second `int` rather than the first. One the label does not contain spans nothing.
        private static ParameterSpan SpanOf(string label, JsonElement parameterLabel, int from)
        {
            if (parameterLabel.ValueKind == JsonValueKind.String)
            {
                var text = parameterLabel.GetString()!;
                var at = text.Length == 0 ? -1 : label.IndexOf(text, Math.Min(from, label.Length), StringComparison.Ordinal);
                return at < 0 ? new ParameterSpan(0, 0) : new ParameterSpan(at, text.Length);
            }

            if (parameterLabel.ValueKind == JsonValueKind.Array && parameterLabel.GetArrayLength() == 2)
            {
                var start = Math.Clamp(parameterLabel[0].AsCount("a parameter start"), 0, label.Length);
                var end = Math.Clamp(parameterLabel[1].AsCount("a parameter end"), start, label.Length);
                return new ParameterSpan(start, end - start);
            }

            throw new LspParseException($"a parameter label must be a string or two offsets, was {parameterLabel.ValueKind}");
        }

        private static string? DocumentationOf(JsonElement signature) => signature.Optional("documentation") switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            { ValueKind: JsonValueKind.Object } markup when markup.Optional("value") is { ValueKind: JsonValueKind.String } value
                => value.GetString(),
            _ => null,
        };

        private static int? Count(JsonElement owner, string name) =>
            owner.Optional(name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var value) && value >= 0
                ? value
                : null;
    }
}

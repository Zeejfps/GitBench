namespace GitBench.Features.CodeIntel;

internal enum SymbolKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Record,
    Enum,
    Method,
    Constructor,
    Property,
    Event,
    Field,
    EnumMember,
    Function,
    Type,
}

internal static class SymbolKinds
{
    public static IReadOnlyList<string> CaptureSuffixes { get; } =
    [
        "namespace", "class", "struct", "interface", "record", "enum", "method", "constructor",
        "property", "event", "field", "enum_member", "function", "type",
    ];

    public static bool TryParseCaptureSuffix(ReadOnlySpan<char> suffix, out SymbolKind kind)
    {
        switch (suffix)
        {
            case "namespace": kind = SymbolKind.Namespace; return true;
            case "class": kind = SymbolKind.Class; return true;
            case "struct": kind = SymbolKind.Struct; return true;
            case "interface": kind = SymbolKind.Interface; return true;
            case "record": kind = SymbolKind.Record; return true;
            case "enum": kind = SymbolKind.Enum; return true;
            case "method": kind = SymbolKind.Method; return true;
            case "constructor": kind = SymbolKind.Constructor; return true;
            case "property": kind = SymbolKind.Property; return true;
            case "event": kind = SymbolKind.Event; return true;
            case "field": kind = SymbolKind.Field; return true;
            case "enum_member": kind = SymbolKind.EnumMember; return true;
            case "function": kind = SymbolKind.Function; return true;
            case "type": kind = SymbolKind.Type; return true;
            default: kind = default; return false;
        }
    }
}

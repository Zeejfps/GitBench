using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;

namespace GitBench.Features.Search;

/// <summary>
/// One declaration somewhere in the repository: what it is called, what it is, what it sits in, and
/// where its name is. The same shape whether the symbol index found it or a language server did.
/// </summary>
/// <param name="Container">The type a member belongs to, or null for a top-level declaration.</param>
/// <param name="ParameterTypes">An overload's parameter types, as the outline renders them.</param>
/// <param name="Path">Repo-relative, with forward slashes.</param>
internal sealed record SymbolRow(
    string Name,
    SymbolKind Kind,
    string? Container,
    string? ParameterTypes,
    string Path,
    FileLine Line,
    RawColumn Column)
{
    public bool IsType => SearchKinds.IsType(Kind);
}

internal static class SearchKinds
{
    /// <summary>What the Types tab lists: the things a name can be a type of.</summary>
    public static bool IsType(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Record
            or SymbolKind.Enum or SymbolKind.Type => true,
        SymbolKind.Namespace or SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Property
            or SymbolKind.Event or SymbolKind.Field or SymbolKind.EnumMember or SymbolKind.Function
            or SymbolKind.Other => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not classified."),
    };
}

using GitBench.Features.CodeIntel;
using GitBench.Lsp;

namespace GitBench.Features.LanguageServers;

/// <summary>Where a server's symbol kinds become the app's: nothing past here sees the protocol's.</summary>
internal static class WorkspaceSymbolKinds
{
    public static SymbolKind ToApp(LspSymbolKind kind) => kind switch
    {
        LspSymbolKind.Module or LspSymbolKind.Namespace or LspSymbolKind.Package => SymbolKind.Namespace,
        LspSymbolKind.Class => SymbolKind.Class,
        LspSymbolKind.Method or LspSymbolKind.Operator => SymbolKind.Method,
        LspSymbolKind.Property => SymbolKind.Property,
        LspSymbolKind.Field => SymbolKind.Field,
        LspSymbolKind.Constructor => SymbolKind.Constructor,
        LspSymbolKind.Enum => SymbolKind.Enum,
        LspSymbolKind.Interface => SymbolKind.Interface,
        LspSymbolKind.Function => SymbolKind.Function,
        LspSymbolKind.EnumMember => SymbolKind.EnumMember,
        LspSymbolKind.Struct => SymbolKind.Struct,
        LspSymbolKind.Event => SymbolKind.Event,
        LspSymbolKind.Unknown or LspSymbolKind.File or LspSymbolKind.Variable or LspSymbolKind.Constant
            or LspSymbolKind.String or LspSymbolKind.Number or LspSymbolKind.Boolean or LspSymbolKind.Array
            or LspSymbolKind.Object or LspSymbolKind.Key or LspSymbolKind.Null
            or LspSymbolKind.TypeParameter => SymbolKind.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not mapped."),
    };
}

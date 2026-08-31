using GitBench.Features.CodeIntel;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// TypeScript and TSX outlines. Two grammars out of one checkout, and two <c>.scm</c> files with
/// the same contents — TSX is separate because JSX syntax is ambiguous with type assertions, so
/// the same bytes parse two ways and only the extension says which.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class TypeScriptOutlineTests(CodeIntelFixture fixture)
{
    private const string Source = """
        export namespace App {
          export interface Session {
            id: string;
            renew(ttl: number): void;
          }

          export enum Kind { Read, Write = 2 }

          export type Handler = (s: Session) => void;

          export abstract class AuthService {
            private tries = 0;

            constructor(private readonly seed: string) {}

            abstract check(user: string): boolean;

            login(user: string, attempt: number): boolean {
              return true;
            }
          }
        }

        export const useAuth = (id: string) => {
          return id;
        };

        const config = { a: 1 };

        export function run(a: string, b?: number): void {}
        """;

    [Fact]
    public void EveryDeclarationFormIsFoundAndNested()
    {
        var outline = fixture.Outline(Source, CodeLanguage.TypeScript);

        Assert.Equal(
            [("App", SymbolKind.Namespace, 0),
             ("Session", SymbolKind.Interface, 1),
             ("id", SymbolKind.Property, 2),
             ("renew", SymbolKind.Method, 2),
             ("Kind", SymbolKind.Enum, 1),
             ("Read", SymbolKind.EnumMember, 2),
             ("Write", SymbolKind.EnumMember, 2),
             ("Handler", SymbolKind.Type, 1),
             ("AuthService", SymbolKind.Class, 1),
             ("tries", SymbolKind.Field, 2),
             ("constructor", SymbolKind.Method, 2),
             ("check", SymbolKind.Method, 2),
             ("login", SymbolKind.Method, 2),
             ("useAuth", SymbolKind.Function, 0),
             ("config", SymbolKind.Field, 0),
             ("run", SymbolKind.Function, 0)],
            Flatten(outline.Roots, 0).ToArray());
    }

    // The dominant shape in modern TypeScript and every React component. It reads as a function
    // rather than a binding, and the plain `const config = ...` beside it still reads as a value —
    // the two patterns are disjoint by structure, because matches arrive in source order rather
    // than in the order the patterns are written.
    [Fact]
    public void AConstArrowFunctionIsAFunctionAndAConstValueIsNot()
    {
        var outline = fixture.Outline(Source, CodeLanguage.TypeScript);

        Assert.Equal(SymbolKind.Function, Find(outline, "useAuth").Kind);
        Assert.Equal(SymbolKind.Field, Find(outline, "config").Kind);
    }

    // The annotation node carries its own colon, so an unwrapped one would read ": string".
    [Fact]
    public void ParameterTypesDropTheirNamesAndTheirAnnotationColon()
    {
        var outline = fixture.Outline(Source, CodeLanguage.TypeScript);

        Assert.Equal("string, number", Find(outline, "login").ParameterTypes);
        Assert.Equal("number", Find(outline, "renew").ParameterTypes);
        // Including the parameter properties a constructor declares its fields with.
        Assert.Equal("string", Find(outline, "constructor").ParameterTypes);
    }

    [Fact]
    public void ADeclarationWithNoBodyIsNotFoldable()
    {
        var outline = fixture.Outline(Source, CodeLanguage.TypeScript);

        var abstractMethod = Find(outline, "check");
        Assert.Equal(abstractMethod.EndLine, abstractMethod.SignatureEndLine);

        var withBody = Find(outline, "login");
        Assert.True(withBody.SignatureEndLine < withBody.EndLine);
    }

    // JSX is the whole reason TSX is its own grammar: parsed as TypeScript this is a type
    // assertion followed by nonsense, and the outline would come back wrong or empty.
    [Fact]
    public void TsxParsesAComponentThatTypeScriptCannot()
    {
        const string component = """
            export const Badge = (props: { label: string }) => {
              return <span className="badge">{props.label}</span>;
            };

            export function Panel() {
              return <Badge label="hi" />;
            }
            """;

        var outline = fixture.Outline(component, CodeLanguage.Tsx);

        Assert.Equal(
            [("Badge", SymbolKind.Function, 0), ("Panel", SymbolKind.Function, 0)],
            Flatten(outline.Roots, 0).ToArray());
    }

    [Fact]
    public void TheExtensionPicksTheGrammar()
    {
        Assert.Equal(CodeLanguage.TypeScript, CodeLanguages.Detect("src/auth.ts"));
        Assert.Equal(CodeLanguage.TypeScript, CodeLanguages.Detect("src/auth.mts"));
        Assert.Equal(CodeLanguage.Tsx, CodeLanguages.Detect("src/Badge.tsx"));
        Assert.Null(CodeLanguages.Detect("src/auth.js"));
    }

    private static IEnumerable<(string Name, SymbolKind Kind, int Depth)> Flatten(
        IReadOnlyList<OutlineNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            yield return (node.Name, node.Kind, depth);
            foreach (var child in Flatten(node.Children, depth + 1)) yield return child;
        }
    }

    private static OutlineNode Find(FileOutline outline, string name) =>
        outline.Flatten().Single(n => n.Name == name);
}

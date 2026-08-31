using GitBench.Features.CodeIntel;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// One outline per bundled grammar. Not exhaustive per language — the C# and TypeScript suites do
/// that for the two the app was built around — but enough that a grammar pin bump which renames a
/// node fails here rather than silently returning nothing, which is the failure §10 warns about and
/// the only one a query can have without any error at all.
/// </summary>
/// <remarks>
/// Each case names the shapes that language actually has, because the point is that the query knows
/// the grammar: Go's methods are not its functions, Rust's impl block is not its struct, and a C
/// prototype is a declaration in its own right.
/// </remarks>
[Collection(nameof(CodeIntelCollection))]
public class BundledLanguageOutlineTests(CodeIntelFixture fixture)
{
    [Fact]
    public void EveryBundledLanguageIsCoveredHere()
    {
        var covered = Cases.Select(c => c.Language).ToHashSet();
        // C# and TypeScript/TSX have suites of their own.
        covered.UnionWith([CodeLanguage.CSharp, CodeLanguage.TypeScript, CodeLanguage.Tsx]);

        Assert.Empty(CodeLanguages.All.Where(l => !covered.Contains(l)));
    }

    // One test rather than a Theory: CodeLanguage is internal, so it cannot cross a public
    // MemberData boundary. Every language is checked and every mismatch reported together, which
    // beats fixing them one failing run at a time.
    [Fact]
    public void TheOutlineNamesWhatEachLanguageDeclares()
    {
        var wrong = new List<string>();
        foreach (var (language, source, expected) in Cases)
        {
            var actual = Flatten(fixture.Outline(source, language).Roots, 0).ToArray();
            if (!actual.SequenceEqual(expected))
            {
                wrong.Add($"{language}: expected [{Describe(expected)}] but got [{Describe(actual)}]");
            }
        }

        Assert.Empty(wrong);
    }

    private static string Describe(IEnumerable<(string Name, SymbolKind Kind, int Depth)> nodes) =>
        string.Join(", ", nodes.Select(n => $"{n.Depth}:{n.Kind} {n.Name}"));

    private static readonly (CodeLanguage Language, string Source, (string, SymbolKind, int)[] Expected)[] Cases =
    [
        (CodeLanguage.JavaScript, """
            export class Auth {
              login(user) { return user; }
            }
            export const useAuth = (id) => { return id; };
            const config = { a: 1 };
            """,
            [("Auth", SymbolKind.Class, 0),
             ("login", SymbolKind.Method, 1),
             ("useAuth", SymbolKind.Function, 0),
             ("config", SymbolKind.Field, 0)]),

        // Python draws no line between a function and a method; the nesting says which it is.
        (CodeLanguage.Python, """
            MAX_TRIES = 3

            class AuthService:
                def login(self, user):
                    pass
            """,
            [("MAX_TRIES", SymbolKind.Field, 0),
             ("AuthService", SymbolKind.Class, 0),
             ("login", SymbolKind.Function, 1)]),

        // A method hangs off a receiver, which is not the same node as a function.
        (CodeLanguage.Go, """
            package auth

            type Session struct {
                ID string
            }

            type Store interface {
                Get(id string) Session
            }

            func Run(a string) error { return nil }

            func (s *Session) Renew(ttl int) {}
            """,
            [("Session", SymbolKind.Struct, 0),
             ("ID", SymbolKind.Field, 1),
             ("Store", SymbolKind.Interface, 0),
             ("Run", SymbolKind.Function, 0),
             ("Renew", SymbolKind.Method, 0)]),

        // The impl block is its own declaration, sharing a name with the struct it implements.
        (CodeLanguage.Rust, """
            mod auth {
                pub struct Session { pub id: String }

                pub enum Kind { Read, Write }

                impl Session {
                    pub fn renew(&self, ttl: u32) {}
                }
            }
            """,
            [("auth", SymbolKind.Namespace, 0),
             ("Session", SymbolKind.Struct, 1),
             ("id", SymbolKind.Field, 2),
             ("Kind", SymbolKind.Enum, 1),
             ("Read", SymbolKind.EnumMember, 2),
             ("Write", SymbolKind.EnumMember, 2),
             ("Session", SymbolKind.Class, 1),
             ("renew", SymbolKind.Function, 2)]),

        (CodeLanguage.Java, """
            package app;

            public class AuthService {
                private int tries;

                public AuthService(String seed) {}

                public boolean login(String user) { return true; }
            }

            enum Kind { READ, WRITE }
            """,
            [("AuthService", SymbolKind.Class, 0),
             ("tries", SymbolKind.Field, 1),
             ("AuthService", SymbolKind.Constructor, 1),
             ("login", SymbolKind.Method, 1),
             ("Kind", SymbolKind.Enum, 0),
             ("READ", SymbolKind.EnumMember, 1),
             ("WRITE", SymbolKind.EnumMember, 1)]),

        // A prototype and a definition are two declarations, and a specifier with no body — the
        // `struct Session` in a typedef or a variable declaration — is a reference and neither.
        (CodeLanguage.C, """
            typedef struct Session Session;

            struct Session {
                int id;
            };

            int run(const char *a);

            int run(const char *a) {
                return 0;
            }
            """,
            [("Session", SymbolKind.Type, 0),
             ("Session", SymbolKind.Struct, 0),
             ("id", SymbolKind.Field, 1),
             ("run", SymbolKind.Function, 0),
             ("run", SymbolKind.Function, 0)]),

        (CodeLanguage.Bash, """
            MAX_TRIES=3

            run() {
              echo "hi"
            }
            """,
            [("MAX_TRIES", SymbolKind.Field, 0),
             ("run", SymbolKind.Function, 0)]),

        // The key's quotes belong to the syntax, not to the name.
        (CodeLanguage.Json, """
            { "name": "gitbench", "scripts": { "build": "dotnet build" } }
            """,
            [("name", SymbolKind.Field, 0),
             ("scripts", SymbolKind.Field, 0),
             ("build", SymbolKind.Field, 1)]),

        (CodeLanguage.Yaml, """
            name: build
            jobs:
              test:
                runs-on: ubuntu-latest
            """,
            [("name", SymbolKind.Field, 0),
             ("jobs", SymbolKind.Field, 0),
             ("test", SymbolKind.Field, 1),
             ("runs-on", SymbolKind.Field, 2)]),

        (CodeLanguage.Css, """
            .badge { color: red; }

            #main > .row {
              display: flex;
            }
            """,
            [(".badge", SymbolKind.Type, 0),
             ("#main > .row", SymbolKind.Type, 0)]),

        (CodeLanguage.Html, """
            <html>
              <body>
                <div class="row"><span>hi</span></div>
              </body>
            </html>
            """,
            [("html", SymbolKind.Type, 0),
             ("body", SymbolKind.Type, 1),
             ("div", SymbolKind.Type, 2),
             ("span", SymbolKind.Type, 3)]),

        // The section is the declaration, not the heading, so folding one takes the prose with it.
        (CodeLanguage.Markdown, """
            # Title

            Some prose.

            ## Section one

            More prose.
            """,
            [("Title", SymbolKind.Type, 0),
             ("Section one", SymbolKind.Type, 1)]),
    ];

    private static IEnumerable<(string, SymbolKind, int)> Flatten(IReadOnlyList<OutlineNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            yield return (node.Name, node.Kind, depth);
            foreach (var child in Flatten(node.Children, depth + 1)) yield return child;
        }
    }
}

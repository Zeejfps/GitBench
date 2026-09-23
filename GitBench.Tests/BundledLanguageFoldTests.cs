using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// One fold query per bundled grammar, and what each folds in a file shaped the way that language
/// is written. A fold query that names a node the grammar renamed compiles and matches nothing, so
/// the lines a sample folds from are what catches it.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class BundledLanguageFoldTests(CodeIntelFixture fixture)
{
    [Fact]
    public void EveryBundledLanguageFoldsMoreThanItsDeclarations()
    {
        var log = new List<string>();
        using var grammars = new TreeSitterGrammars(log.Add);

        Assert.Empty(CodeLanguages.All.Where(l => grammars.Get(l)?.Folds is null));
        Assert.DoesNotContain(log, l => l.Contains("Folding", StringComparison.Ordinal));
        Assert.Empty(CodeLanguages.All.Where(l => Cases.All(c => c.Language != l)));
    }

    // One test rather than a Theory, as in BundledLanguageOutlineTests: every mismatch at once.
    [Fact]
    public void EachLanguageFoldsFromTheLinesItsConstructsOpenOn()
    {
        var wrong = new List<string>();
        foreach (var (language, source, expected) in Cases)
        {
            var actual = ChevronLines(source, language);
            if (!actual.SequenceEqual(expected))
                wrong.Add($"{language}: expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // Nothing closes a Python block: its last line is a statement, so folding hides it with the rest
    // rather than pulling it up behind the chip.
    [Fact]
    public void APythonBlockHidesItsLastLineRatherThanJoiningIt()
    {
        const string source = """
            def run(items):
                for item in items:
                    check(item)
                    issue(item)
                done()
            """;

        var rows = Collapse(source, CodeLanguage.Python, "    for item in items:");

        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        Assert.IsType<FoldChip.Interior>(chip.Fold!.Value.Chip);
        Assert.DoesNotContain(rows, r => Text(r) == "        issue(item)");
        Assert.Contains(rows, r => Text(r) == "    done()");
    }

    // The elif is a fold of its own, so folding the if stops short of it.
    [Fact]
    public void APythonIfFoldsOnlyItsOwnBranch()
    {
        const string source = """
            if ready:
                start()
                watch()
            elif waiting:
                queue()
                watch()
            """;

        var rows = Collapse(source, CodeLanguage.Python, "if ready:");

        Assert.DoesNotContain(rows, r => Text(r) == "    start()");
        Assert.Contains(rows, r => Text(r) == "elif waiting:");
        Assert.Contains(rows, r => Text(r) == "    queue()");
    }

    // A Markdown section ends on its last paragraph, not a closing bracket.
    [Fact]
    public void AMarkdownSectionHidesItsLastLine()
    {
        const string source = """
            # Setup

            Install it.

            Then run it.
            """;

        var rows = Collapse(source, CodeLanguage.Markdown, "# Setup");

        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        Assert.IsType<FoldChip.Interior>(chip.Fold!.Value.Chip);
        Assert.DoesNotContain(rows, r => Text(r) == "Then run it.");
    }

    // A brace on a line of its own folds from the line it belongs to, and leaves no lone brace.
    [Fact]
    public void AnAllmanBlockFoldsFromItsHeader()
    {
        const string source = """
            class A
            {
                void Run()
                {
                    if (ready)
                    {
                        Start();
                        Watch();
                    }
                }
            }
            """;

        var rows = Collapse(source, CodeLanguage.CSharp, "        if (ready)");

        Assert.DoesNotContain(rows, r => Text(r) == "        {");
        Assert.DoesNotContain(rows, r => Text(r) == "            Start();");
        var chip = Assert.Single(rows.OfType<DiffRow.Line>(), r => r.Fold is { Chip: not null });
        Assert.Equal("        if (ready)", chip.Text.Raw);
        Assert.Equal("}", Assert.IsType<FoldChip.Joined>(chip.Fold!.Value.Chip).Text);
    }

    private IReadOnlyList<DiffRow> Collapse(string source, CodeLanguage language, string line)
    {
        var open = Rows(source, language, FoldState.Open(PathOf(language)));
        var id = open.OfType<DiffRow.Line>().Single(r => r.Text.Raw == line).Fold!.Value.Id;
        return Rows(source, language, FoldState.Open(PathOf(language)).Toggled(id));
    }

    private int[] ChevronLines(string source, CodeLanguage language) =>
        Rows(source, language, FoldState.Open(PathOf(language)))
            .OfType<DiffRow.Line>()
            .Where(r => r.Fold is { Chevron: true })
            .Select(r => r.NewNumber.Line!.Value.Value)
            .ToArray();

    private IReadOnlyList<DiffRow> Rows(string source, CodeLanguage language, FoldState folds)
    {
        var state = new DiffRenderState.FullFile(
            PathOf(language),
            source.ReplaceLineEndings("\n").Split('\n'),
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(null, fixture.Outline(source, language), null));
        return DiffRowSet.Build(state, new LocalizationService(new State<Locale>(Locale.En)), folds).Rows;
    }

    private static string PathOf(CodeLanguage language) => $"sample.{language}";

    private static string Text(DiffRow row) => row is DiffRow.Line line ? line.Text.Raw : string.Empty;

    private static readonly (CodeLanguage Language, string Source, int[] Expected)[] Cases =
    [
        (CodeLanguage.CSharp, """
            class Auth
            {
                void Login(string user)
                {
                    if (user is null)
                    {
                        Fail();
                        return;
                    }
                    var roles = new[]
                    {
                        "admin",
                        "user",
                    };
                }
            }
            """, [1, 3, 5, 10]),

        (CodeLanguage.TypeScript, """
            import {
              a,
              b,
            } from "x";
            export function run(list: number[]) {
              for (const n of list) {
                check(n);
                issue(n);
              }
            }
            """, [1, 5, 6]),

        (CodeLanguage.Tsx, """
            export function Panel() {
              return (
                <div>
                  <span>hi</span>
                </div>
              );
            }
            """, [1, 2, 3]),

        (CodeLanguage.JavaScript, """
            const config = {
              port: 80,
              host: "x",
            };
            items.forEach((item) => {
              check(item);
              issue(item);
            });
            """, [1, 5]),

        (CodeLanguage.Json, """
            {
              "name": "app",
              "scripts": {
                "build": "tsc",
                "test": "jest"
              },
              "files": [
                "dist",
                "src"
              ]
            }
            """, [1, 3, 7]),

        (CodeLanguage.Css, """
            .card {
              color: red;
              margin: 0;
            }
            @media (min-width: 600px) {
              .card {
                color: blue;
                margin: 4px;
              }
            }
            """, [1, 5, 6]),

        (CodeLanguage.Html, """
            <html>
              <body>
                <ul>
                  <li>one</li>
                  <li>two</li>
                </ul>
              </body>
            </html>
            """, [1, 2, 3]),

        (CodeLanguage.Markdown, """
            # Setup

            ```sh
            npm install
            npm test
            ```

            ## Run

            Run it.
            """, [1, 3, 8]),

        (CodeLanguage.Yaml, """
            server:
              port: 80
              hosts:
                - a
                - b
            jobs:
              - name: build
                run: make
            """, [1, 3, 6]),

        (CodeLanguage.Python, """
            def run(items):
                for item in items:
                    check(item)
                    issue(item)
                config = {
                    "a": 1,
                    "b": 2,
                }
                if ready:
                    start()
                    watch()
                else:
                    stop()
                    wait()
            """, [1, 2, 5, 9, 12]),

        (CodeLanguage.Go, """
            package main

            import (
            	"fmt"
            	"os"
            )

            func run(items []string) {
            	for _, item := range items {
            		fmt.Println(item)
            		os.Exit(0)
            	}
            	switch len(items) {
            	case 0:
            		fmt.Println("none")
            		os.Exit(1)
            	}
            }
            """, [3, 8, 9, 13, 14]),

        (CodeLanguage.Rust, """
            fn run(items: &[u8]) {
                for item in items {
                    check(item);
                    issue(item);
                }
                match items.len() {
                    0 => stop(),
                    _ => go(),
                }
            }
            """, [1, 2, 6]),

        (CodeLanguage.Java, """
            class Auth {
                void login(String user) {
                    if (user == null) {
                        fail();
                        return;
                    }
                    int[] codes = {
                        1,
                        2,
                    };
                }
            }
            """, [1, 2, 3, 7]),

        (CodeLanguage.Bash, """
            deploy() {
              for host in a b; do
                ssh "$host" true
                echo "$host"
              done
            }
            if [ -f x ]; then
              echo yes
              echo again
            fi
            """, [1, 2, 7]),

        (CodeLanguage.C, """
            int run(int n)
            {
                if (n > 0)
                {
                    start();
                    watch();
                }
                int codes[] = {
                    1,
                    2,
                };
                return 0;
            }
            """, [1, 3, 8]),

        (CodeLanguage.Toml, """
            [package]
            name = "app"
            version = "1.0"

            [dependencies]
            serde = "1"
            list = [
              "a",
              "b",
            ]
            """, [1, 5, 7]),

        (CodeLanguage.Svelte, """
            <script>
              let items = [];
              let ready = false;
            </script>

            {#if ready}
              <ul>
                <li>one</li>
                <li>two</li>
              </ul>
            {/if}
            """, [1, 6, 7]),
    ];
}

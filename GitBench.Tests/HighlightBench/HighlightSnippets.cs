using GitBench.Features.CodeIntel;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// Constructs where a regex state machine and a parser are expected to disagree — contextual
/// keywords, nested string interpolation, generics against comparison operators, and the rest of
/// the places where the right answer depends on the enclosing grammar rule rather than on the
/// preceding characters.
/// </summary>
internal static class HighlightSnippets
{
    public static IReadOnlyList<(CodeLanguage Language, string LanguageId, string Source)> All { get; } =
    [
        (CodeLanguage.CSharp, "csharp", Lf(
            """""
            record Point(int X, int Y);
            var record = 1;                       // contextual keyword used as an identifier
            var value = $"user {user.Name,-8:D} and {(flag ? "yes" : "no")}";
            var raw = """He said "hi" to me""";
            if (a < b && c > d) Swap(a, b);       // comparison, not a type argument list
            var list = new Dictionary<string, List<int>>();
            var result = obj switch { > 0 and < 10 => "small", _ => nameof(obj) };
            [Obsolete("gone")] static int Local(scoped ReadOnlySpan<char> s) => s.Length;
            """"")),

        (CodeLanguage.TypeScript, "typescript", Lf(
            """
            const enum Mode { On, Off }
            type Pair<T> = { left: T; right: T };
            const a = x < y && y > z ? 1 : 2;
            const t = `hello ${user.name} you have ${count + 1} messages`;
            function pick<K extends keyof T, T>(o: T, k: K): T[K] { return o[k]; }
            const satisfies = 1; const type = 2;
            class Box { #secret = 0; get value(): number { return this.#secret; } }
            """)),

        (CodeLanguage.Python, "python", Lf(
            """
            match = 1
            case = 2
            def greet(name: str, *args: int, **kw: object) -> "Greeting":
                print(f"hi {name!r:>{width}} and {items[0]['key']}")
                return type("Greeting", (), {})
            @functools.cache
            async def run(): await asyncio.sleep(0)
            """)),

        (CodeLanguage.Rust, "rust", Lf(
            """
            pub fn longest<'a>(x: &'a str, y: &'a str) -> &'a str { if x.len() > y.len() { x } else { y } }
            let v: Vec<Box<dyn Fn(u32) -> u32>> = vec![];
            let raw = r#"a "quoted" thing"#;
            #[derive(Debug, Clone)]
            struct Wrapper(HashMap<String, Vec<u8>>);
            macro_rules! shout { ($x:expr) => { println!("{}", $x) } }
            """)),

        (CodeLanguage.Go, "go", Lf(
            """
            type User struct {
                Name string `json:"name" db:"user_name"`
            }
            func Map[T any, U any](in []T, f func(T) U) []U { return nil }
            const raw = `line one
            line two`
            ch := make(chan<- int, 8)
            """)),

        (CodeLanguage.JavaScript, "javascript", Lf(
            """
            const re = /ab+c\/[a-z]*/gi;
            const div = a / b / c;
            const el = <div className="x">{items.map(i => <Item key={i} />)}</div>;
            label: for (const x of xs) { if (x) break label; }
            const { a = 1, ...rest } = obj ?? {};
            """)),
    ];

    private static string Lf(string text) => text.ReplaceLineEndings("\n");
}

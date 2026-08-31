# Tree-sitter vs TextMate highlighting

- Host: Microsoft Windows NT 10.0.26200.0, 24 logical cores, .NET 10.0.2, Release build
- Corpus: this checkout, 120 files per language max, best of 3 runs per file
- Generated: 2026-08-31 16:49

## Query compilation

| Language | Patterns kept | Patterns in file | Note |
| --- | --- | --- | --- |
| CSharp | 35 | 35 |  |
| TypeScript | 32 | 34 | patterns dropped, see below |
| Tsx | 32 | 34 | patterns dropped, see below |
| JavaScript | 25 | 27 | patterns dropped, see below |
| Json | 6 | 6 |  |
| Css | 27 | 27 |  |
| Html | 7 | 7 |  |
| Markdown | 11 | 11 |  |
| Yaml | 15 | 15 |  |
| Python | 19 | 19 |  |
| Go | 15 | 15 |  |
| Rust | 43 | 43 |  |
| Java | 24 | 24 |  |
| Bash | 10 | 10 |  |
| C | 18 | 18 |  |

## Throughput

Per-file wall time in milliseconds, best of three.

| Language | Files | KB | TextMate med | TextMate p95 | TextMate max | tree-sitter med | tree-sitter p95 | tree-sitter max | Speedup (total) | TM plain |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| CSharp | 120 | 548 | 10.87 | 58.21 | 249.21 | 1.16 | 5.83 | 35.72 | 9.7x | 0 |
| TypeScript | 39 | 586 | 6.82 | 155.26 | 156.16 | 0.50 | 7.86 | 71.58 | 9.4x | 1 |
| JavaScript | 120 | 809 | 1.94 | 216.03 | 750.73 | 0.10 | 10.19 | 95.14 | 14.0x | 2 |
| Json | 120 | 5206 | 0.79 | 54.71 | 74.79 | 0.48 | 12.79 | 19.67 | 3.4x | 4 |
| Css | 5 | 314 | 58.50 | 750.34 | 750.34 | 2.98 | 58.55 | 58.55 | 13.3x | 2 |
| Html | 7 | 27 | 0.88 | 49.65 | 49.65 | 0.08 | 34.44 | 34.44 | 2.3x | 0 |
| Markdown | 120 | 659 | 2.76 | 17.36 | 76.70 | 0.61 | 4.05 | 17.90 | 4.2x | 0 |
| Yaml | 114 | 147 | 1.04 | 4.19 | 14.77 | 0.10 | 0.37 | 1.12 | 11.6x | 0 |
| Python | 82 | 248 | 2.34 | 102.12 | 151.87 | 0.16 | 6.26 | 9.54 | 15.7x | 0 |
| Go | 33 | 210 | 0.26 | 79.95 | 137.24 | 0.05 | 13.88 | 16.42 | 7.2x | 0 |
| Rust | 120 | 1736 | 13.51 | 164.38 | 362.71 | 0.98 | 11.29 | 26.47 | 14.8x | 0 |
| Java | 3 | 3 | 0.72 | 10.06 | 10.06 | 0.08 | 0.34 | 0.34 | 24.0x | 0 |
| Bash | 11 | 66 | 1.98 | 25.23 | 25.23 | 0.75 | 7.10 | 7.10 | 3.4x | 0 |
| C | 120 | 1651 | 4.83 | 214.06 | 750.48 | 0.42 | 12.37 | 108.81 | 16.0x | 2 |

**Whole corpus:** 1014 files, 11.9 MB. TextMate 21775 ms (0.5 MB/s), tree-sitter 2054 ms (5.8 MB/s) — **10.6x**.

## Is tree-sitter ever the slower one?

| Language | Files where tree-sitter is slower | Worst ratio (files TextMate actually tokenized) |
| --- | --- | --- |
| CSharp | 0 / 120 | `GitBench\Localization\Locale.cs` 0.05 ms vs 0.09 ms (0.50x) |
| TypeScript | 0 / 39 | `external\cs_tree_sitter\native\vendor\tree-sitter\lib\binding_web\src\index.ts` 0.15 ms vs 0.68 ms (0.22x) |
| JavaScript | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter-json\eslint.config.mjs` 0.04 ms vs 0.30 ms (0.14x) |
| Json | 0 / 120 | `GitBench\Localization\Strings\zh-Hans.json` 2.36 ms vs 6.82 ms (0.35x) |
| Css | 0 / 5 | `external\cs_tree_sitter\native\vendor\tree-sitter-css\test\highlight\test_css.css` 0.41 ms vs 5.17 ms (0.08x) |
| Html | 0 / 7 | `external\cs_tree_sitter\native\vendor\tree-sitter-html\examples\deeply-nested-custom.html` 34.44 ms vs 42.86 ms (0.80x) |
| Markdown | 4 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter-yaml\README.md` 0.53 ms vs 0.50 ms (1.06x) |
| Yaml | 0 / 114 | `external\cs_tree_sitter\native\vendor\tree-sitter-c\.github\ISSUE_TEMPLATE\config.yml` 0.01 ms vs 0.04 ms (0.38x) |
| Python | 0 / 82 | `external\cs_tree_sitter\native\vendor\tree-sitter-python\examples\compound-statement-without-trailing-newline.py` 0.01 ms vs 0.07 ms (0.19x) |
| Go | 0 / 33 | `external\cs_tree_sitter\native\vendor\tree-sitter-typescript\bindings\go\tsx.go` 0.04 ms vs 0.18 ms (0.24x) |
| Rust | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter\crates\loader\build.rs` 0.10 ms vs 0.65 ms (0.15x) |
| Java | 0 / 3 | `external\cs_tree_sitter\native\vendor\tree-sitter-java\test\highlight\types.java` 0.08 ms vs 0.72 ms (0.11x) |
| Bash | 0 / 11 | `external\cs_tree_sitter\native\vendor\tree-sitter-bash\examples\update-authors.sh` 0.03 ms vs 0.05 ms (0.58x) |
| C | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter-c-sharp\bindings\c\tree-sitter-c-sharp.h` 0.07 ms vs 0.30 ms (0.22x) |

## Where the tree-sitter time goes

Capability A already parses every file it shows a hunk header for. The parse column is the cost that is already being paid; query plus build is the marginal cost of highlighting from that tree.

| Language | Parse ms | Query ms | Build ms | Marginal share | TextMate ms vs marginal |
| --- | --- | --- | --- | --- | --- |
| CSharp | 182 | 40 | 7 | 20% | 47.7x |
| TypeScript | 78 | 42 | 3 | 36% | 26.1x |
| JavaScript | 160 | 97 | 10 | 39% | 36.0x |
| Json | 213 | 126 | 14 | 40% | 8.5x |
| Css | 69 | 23 | 3 | 24% | 54.5x |
| Html | 52 | 2 | 0 | 4% | 59.2x |
| Markdown | 134 | 20 | 2 | 14% | 29.8x |
| Yaml | 10 | 4 | 1 | 33% | 35.3x |
| Python | 33 | 16 | 1 | 35% | 45.4x |
| Go | 24 | 9 | 1 | 29% | 24.5x |
| Rust | 173 | 109 | 10 | 40% | 36.8x |
| Java | 0 | 0 | 0 | 37% | 64.2x |
| Bash | 11 | 3 | 0 | 21% | 16.0x |
| C | 250 | 85 | 7 | 27% | 59.6x |

## Concurrency

500 C# files, 24 workers. TextMate serializes every surface through one lock; tree-sitter parsers are per-worker.

| Engine | 1 thread | 24 threads | Scaling |
| --- | --- | --- | --- |
| TextMate | 9065 ms | 7926 ms | 1.14x |
| tree-sitter | 715 ms | 101 ms | 7.08x |

## Files nobody highlights today

`SyntaxHighlighter.MaxFileChars` is 256 KB; `TreeSitterSymbolExtractor.MaxFileBytes` is 1024 KB. Files between the two render plain today and would not have to.

- Over 256 KB in this checkout: 13 files in a bundled language.
- Between the two caps (plain today, highlightable by tree-sitter): 0 files.

  - `external\cs_tree_sitter\native\vendor\tree-sitter-c-sharp\src\parser.c` — 28969 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-bash\src\parser.c` — 9679 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-typescript\tsx\src\parser.c` — 8564 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-typescript\typescript\src\parser.c` — 8540 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-rust\src\parser.c` — 6353 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-c\src\parser.c` — 3781 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-python\src\parser.c` — 3359 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-javascript\src\parser.c` — 2788 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-java\src\parser.c` — 2501 KB
  - `external\cs_tree_sitter\native\vendor\tree-sitter-markdown\tree-sitter-markdown-inline\src\parser.c` — 2206 KB

## Agreement and coverage

Measured per non-whitespace character of every corpus file, after both engines are reduced to the same `TokenColorSlot` vocabulary.

| Language | TextMate colored | tree-sitter colored | Same slot | Only TextMate | Only tree-sitter | Both, different |
| --- | --- | --- | --- | --- | --- | --- |
| CSharp | 99.9% | 99.8% | 87.4% | 0.2% | 0.0% | 12.3% |
| TypeScript | 93.5% | 98.5% | 82.8% | 1.5% | 6.5% | 9.2% |
| JavaScript | 93.9% | 98.4% | 88.0% | 1.6% | 6.1% | 4.3% |
| Json | 100.0% | 88.1% | 47.8% | 11.9% | 0.0% | 40.2% |
| Css | 93.9% | 92.0% | 59.8% | 2.2% | 0.4% | 31.8% |
| Html | 93.4% | 71.9% | 68.0% | 24.6% | 3.1% | 0.8% |
| Markdown | 33.1% | 10.2% | 6.4% | 22.9% | 0.0% | 3.8% |
| Yaml | 100.0% | 100.0% | 80.9% | 0.0% | 0.0% | 19.1% |
| Python | 77.9% | 88.5% | 64.4% | 11.4% | 22.0% | 2.1% |
| Go | 81.8% | 91.2% | 62.6% | 7.8% | 17.2% | 11.4% |
| Rust | 95.2% | 79.6% | 69.3% | 19.8% | 4.2% | 6.2% |
| Java | 96.3% | 90.4% | 52.5% | 9.6% | 3.7% | 34.2% |
| Bash | 74.1% | 80.3% | 66.6% | 4.5% | 10.7% | 3.0% |
| C | 74.7% | 84.5% | 55.4% | 12.5% | 22.3% | 6.9% |

## Side by side

One letter per column: `K`eyword `S`tring `C`omment `N`umber `T`ype `F`unction `V`ariable `O`perator `P`unctuation con`X`tant, `.` for uncolored.

### CSharp

```text
     record Point(int X, int Y);
  tm KKKKKK TTTTTPKKK VP KKK VPP
  ts KKKKKK TTTTTPTTT VP TTT VPP
     var record = 1;                       // contextual keyword used as an identifier
  tm KKK VVVVVV O NP                       CC CCCCCCCCCC CCCCCCC CCCC CC CC CCCCCCCCCC
  ts KKK VVVVVV O NP                       CC CCCCCCCCCC CCCCCCC CCCC CC CC CCCCCCCCCC
     var value = $"user {user.Name,-8:D} and {(flag ? "yes" : "no")}";
  tm KKK VVVVV O SSSSSS PVVVVPVVVVSONOVP SSS PPVVVV O SSSSS O SSSSPPSP
  ts KKK VVVVV O SSSSSS PVVVVPVVVVPONOSP SSS PPVVVV O SSSSS O SSSSPPSP
     var raw = """He said "hi" to me""";
  tm KKK VVV O SSSSS SSSS SSSS SS SSSSSP
  ts KKK VVV O SSSSS SSSS SSSS SS SSSSSP
     if (a < b && c > d) Swap(a, b);       // comparison, not a type argument list
  tm KK PV O V OO V O VP FFFFPVP VPP       CC CCCCCCCCCCC CCC C CCCC CCCCCCCC CCCC
  ts KK PV O V OO V O VP VVVVPVP VPP       CC CCCCCCCCCCC CCC C CCCC CCCCCCCC CCCC
     var list = new Dictionary<string, List<int>>();
  tm KKK VVVV O OOO TTTTTTTTTTPKKKKKKP TTTTPKKKPPPPP
  ts KKK VVVV O KKK TTTTTTTTTTOTTTTTTP TTTTOTTTOOPPP
     var result = obj switch { > 0 and < 10 => "small", _ => nameof(obj) };
  tm KKK VVVVVV O VVV KKKKKK P O N OOO O NN OO SSSSSSSP V OO OOOOOOPVVVP PP
  ts KKK VVVVVV O VVV KKKKKK P O N ... O NN OO SSSSSSSP . OO VVVVVVPVVVP PP
     [Obsolete("gone")] static int Local(scoped ReadOnlySpan<char> s) => s.Length;
  tm PTTTTTTTTPSSSSSSPP KKKKKK KKK FFFFFPTTTTTT VVVVVVVVVVVV...... .P OO VPVVVVVVP
  ts PVVVVVVVVPSSSSSSPP KKKKKK TTT FFFFFP...... TTTTTTTTTTTTOTTTTO VP OO VPVVVVVVP
```

### TypeScript

```text
     const enum Mode { On, Off }
  tm KKKKK KKKK TTTT P VVP VVV P
  ts KKKKK KKKK TTTT P VVP VVV P
     type Pair<T> = { left: T; right: T };
  tm KKKK TTTTPTP O P VVVVO TP VVVVVO T PP
  ts KKKK TTTTOTO O P VVVV. TP VVVVV. T PP
     const a = x < y && y > z ? 1 : 2;
  tm KKKKK V O V O V OO V O V O N O NP
  ts KKKKK V O V O V OO V O V . N . NP
     const t = `hello ${user.name} you have ${count + 1} messages`;
  tm KKKKK V O SSSSSS PPVVVVPVVVVP SSS SSSS PPVVVVV O NP SSSSSSSSSP
  ts KKKKK V O SSSSSS PPVVVVPVVVVP SSS SSSS PPVVVVV O NP SSSSSSSSSP
     function pick<K extends keyof T, T>(o: T, k: K): T[K] { return o[k]; }
  tm KKKKKKKK FFFFPT KKKKKKK OOOOO TP TPPVO TP VO TPO T.T. P KKKKKK V.V.P P
  ts KKKKKKKK FFFFOT KKKKKKK KKKKK TP TOPV. TP V. TP. TPTP P KKKKKK VPVPP P
     const satisfies = 1; const type = 2;
  tm KKKKK VVVVVVVVV O NP KKKKK VVVV O NP
  ts KKKKK VVVVVVVVV O NP KKKKK VVVV O NP
     class Box { #secret = 0; get value(): number { return this.#secret; } }
  tm KKKKK TTT P VVVVVVV O NP KKK FFFFFPPO TTTTTT P KKKKKK VVVVPVVVVVVVP P P
  ts KKKKK TTT P ....... O NP KKK FFFFFPP. TTTTTT P KKKKKK VVVVP.......P P P
```

### Python

```text
     match = 1
  tm ..... O N
  ts VVVVV O N
     case = 2
  tm .... O N
  ts VVVV O N
     def greet(name: str, *args: int, **kw: object) -> "Greeting":
  tm KKK FFFFFPVVVVP TTTP OVVVVP TTTP OOVVP TTTTTTP PP SSSSSSSSSSP
  ts KKK FFFFF.VVVV. TTT. OVVVV. TTT. OOVV. TTTTTT. OO SSSSSSSSSS.
         print(f"hi {name!r:>{width}} and {items[0]['key']}")
  tm     FFFFFPKSSS X....KKKKX.....XX SSS X.....PNPPSSSSSPXSP
  ts     FFFFF.SSSS PVVVVSSSSSVVVVVSP SSS PVVVVVSNSSSSSSSSPS.
         return type("Greeting", (), {})
  tm     KKKKKK TTTTPSSSSSSSSSSP PPP PPP
  ts     KKKKKK FFFF.SSSSSSSSSS. ... ...
     @functools.cache
  tm PFFFFFFFFFPFFFFF
  ts FVVVVVVVVVFVVVVV
     async def run(): await asyncio.sleep(0)
  tm KKKKK KKK FFFPPP KKKKK .......P.....PNP
  ts KKKKK KKK FFF... KKKKK VVVVVVV.VVVVV.N.
```

### Rust

```text
     pub fn longest<'a>(x: &'a str, y: &'a str) -> &'a str { if x.len() > y.len() { x } else { y } }
  tm KKK KK FFFFFFFPPTPPVO OPT TTTP VO OPT TTTP OO OPT TTT P KK VOFFFPP P VOFFFPP P V P KKKK P V P P
  ts KKK KK FFFFFFFPOVPPVP OOV TTTP VP OOV TTTP .. OOV TTT P KK .PFFFPP . .PFFFPP P . P KKKK P . P P
     let v: Vec<Box<dyn Fn(u32) -> u32>> = vec![];
  tm KKK VO TTTPTTTPKKK TTPTTTP OO TTTPP O FFFFPPP
  ts KKK .P TTTPTTTPKKK TTPTTTP .. TTTPP . FFFFPPP
     let raw = r#"a "quoted" thing"#;
  tm KKK VVV O SSSS SSSSSSSS SSSSSSSP
  ts KKK ... . SSSS SSSSSSSS SSSSSSSP
     #[derive(Debug, Clone)]
  tm PP......PTTTTTP TTTTTPP
  ts VPVVVVVVPTTTTTP TTTTTPP
     struct Wrapper(HashMap<String, Vec<u8>>);
  tm KKKKKK TTTTTTTPTTTTTTTPTTTTTTP TTTPTTPPPP
  ts KKKKKK TTTTTTTPTTTTTTTPTTTTTTP TTTPTTPPPP
     macro_rules! shout { ($x:expr) => { println!("{}", $x) } }
  tm FFFFFFFFFFFF FFFFF P POVOVVVVP OO P FFFFFFFFPSPPSP OVP P P
  ts KKKKKKKKKKKK ..... P P..P....P .. P ........PSSSSP ..P P P
```

### Go

```text
     type User struct {
  tm KKKK TTTT KKKKKK P
  ts KKKK TTTT KKKKKK .
         Name string `json:"name" db:"user_name"`
  tm     .... KKKKKK SSSSSSSSSSSS SSSSSSSSSSSSSSS
  ts     VVVV TTTTTT SSSSSSSSSSSS SSSSSSSSSSSSSSS
     }
  tm P
  ts .
     func Map[T any, U any](in []T, f func(T) U) []U { return nil }
  tm KKKK ...P. ...P . ...PP.. PP.P . KKKKP.P .P PP. P KKKKKK XXX P
  ts KKKK VVV.V TTT. V TTT..VV ..T. V KKKK.T. T. ..T . KKKKKK XXX .
     const raw = `line one
  tm KKKKK VVV O SSSSS SSS
  ts KKKKK VVV O SSSSS SSS
     line two`
  tm SSSS SSSS
  ts SSSS SSSS
     ch := make(chan<- int, 8)
  tm VV OO FFFFPKKKKOO KKKP NP
  ts VV OO VVVV.KKKKOO TTT. N.
```

### JavaScript

```text
     const re = /ab+c\/[a-z]*/gi;
  tm KKKKK VV O SSSOSXXPXXXPOSKKP
  ts KKKKK VV O OSSSSSSSSSSSSOSSP
     const div = a / b / c;
  tm KKKKK VVV O V O V O VP
  ts KKKKK VVV O V O V O VP
     const el = <div className="x">{items.map(i => <Item key={i} />)}</div>;
  tm KKKKK VV O PKKK VVVVVVVVVOSSSPPVVVVVPFFF.V KK PTTTT VVVOPVP PP.PPPKKKPP
  ts KKKKK VV O OVVV VVVVVVVVVOSSSOPVVVVVPFFFPV OO OTTTT VVVOPVP ..PP..VVVOP
     label: for (const x of xs) { if (x) break label; }
  tm .....P KKK .KKKKK V OO VV. P KK .V. KKKKK .....P P
  ts ...... KKK PKKKKK V KK VVP P KK PVP KKKKK .....P P
     const { a = 1, ...rest } = obj ?? {};
  tm KKKKK P V O NP OOOVVVV P O VVV OO PPP
  ts KKKKK P . O NP ...VVVV P O VVV OO PPP
```


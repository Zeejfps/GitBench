# Tree-sitter vs TextMate highlighting

- Host: Microsoft Windows NT 10.0.26200.0, 24 logical cores, .NET 10.0.2, Release build
- Corpus: this checkout, 120 files per language max, best of 3 runs per file
- Generated: 2026-08-31 17:18

Both engines as the app ships them: `TreeSitterSyntaxHighlighter` with its embedded queries, and `SyntaxHighlighter` behind it. Markdown and HTML are absent because they route to TextMate outright — their queries need injections this engine does not run.

## Throughput

Per-file wall time in milliseconds, best of three.

| Language | Files | KB | TextMate med | TextMate p95 | TextMate max | tree-sitter med | tree-sitter p95 | tree-sitter max | Speedup (total) | TM plain |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| CSharp | 120 | 551 | 6.94 | 39.35 | 168.15 | 0.80 | 3.77 | 19.94 | 10.1x | 0 |
| TypeScript | 39 | 586 | 3.73 | 100.19 | 121.72 | 0.25 | 4.70 | 47.26 | 9.4x | 1 |
| JavaScript | 120 | 809 | 1.79 | 132.03 | 750.30 | 0.09 | 5.99 | 43.96 | 21.1x | 1 |
| Json | 120 | 5206 | 0.75 | 53.57 | 73.64 | 0.49 | 12.82 | 20.43 | 3.3x | 4 |
| Css | 5 | 314 | 35.96 | 497.79 | 497.79 | 2.21 | 27.48 | 27.48 | 17.9x | 0 |
| Yaml | 114 | 147 | 1.00 | 4.19 | 14.68 | 0.10 | 0.33 | 1.12 | 11.1x | 0 |
| Python | 83 | 254 | 2.35 | 104.74 | 155.13 | 0.17 | 6.61 | 10.49 | 14.8x | 0 |
| Go | 33 | 210 | 0.27 | 84.41 | 136.75 | 0.05 | 12.68 | 17.43 | 7.3x | 0 |
| Rust | 120 | 1736 | 11.62 | 142.27 | 351.28 | 0.81 | 11.28 | 29.72 | 13.9x | 0 |
| Java | 3 | 3 | 0.68 | 9.65 | 9.65 | 0.07 | 0.35 | 0.35 | 22.7x | 0 |
| Bash | 11 | 66 | 2.08 | 25.18 | 25.18 | 0.87 | 7.41 | 7.41 | 3.2x | 0 |
| C | 120 | 1651 | 4.83 | 179.50 | 724.31 | 0.54 | 11.92 | 115.74 | 11.7x | 2 |

**Whole corpus:** 888 files, 11.3 MB. TextMate 17190 ms (0.7 MB/s), tree-sitter 1583 ms (7.1 MB/s) — **10.9x**.

## Is tree-sitter ever the slower one?

| Language | Files where tree-sitter is slower | Worst ratio (files TextMate actually tokenized) |
| --- | --- | --- |
| CSharp | 0 / 120 | `GitBench\Localization\Locale.cs` 0.04 ms vs 0.08 ms (0.46x) |
| TypeScript | 0 / 39 | `external\cs_tree_sitter\native\vendor\tree-sitter\lib\binding_web\src\index.ts` 0.10 ms vs 0.47 ms (0.20x) |
| JavaScript | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter-python\eslint.config.mjs` 0.02 ms vs 0.16 ms (0.12x) |
| Json | 0 / 120 | `GitBench\Localization\Strings\ja.json` 2.55 ms vs 7.03 ms (0.36x) |
| Css | 0 / 5 | `external\cs_tree_sitter\native\vendor\tree-sitter-css\test\highlight\test_css.css` 0.26 ms vs 2.75 ms (0.09x) |
| Yaml | 0 / 114 | `external\cs_tree_sitter\native\vendor\tree-sitter-rust\.github\ISSUE_TEMPLATE\config.yml` 0.01 ms vs 0.04 ms (0.34x) |
| Python | 0 / 83 | `external\cs_tree_sitter\native\vendor\tree-sitter-python\examples\compound-statement-without-trailing-newline.py` 0.01 ms vs 0.07 ms (0.19x) |
| Go | 0 / 33 | `external\cs_tree_sitter\native\vendor\tree-sitter-bash\bindings\go\binding.go` 0.04 ms vs 0.15 ms (0.25x) |
| Rust | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter\crates\loader\build.rs` 0.05 ms vs 0.40 ms (0.14x) |
| Java | 0 / 3 | `external\cs_tree_sitter\native\vendor\tree-sitter\crates\cli\src\templates\test.java` 0.06 ms vs 0.55 ms (0.11x) |
| Bash | 0 / 11 | `external\cs_tree_sitter\native\vendor\tree-sitter-bash\examples\update-authors.sh` 0.05 ms vs 0.07 ms (0.80x) |
| C | 0 / 120 | `external\cs_tree_sitter\native\vendor\tree-sitter-typescript\bindings\swift\tsx\TreeSitterTSX\tsx.h` 0.10 ms vs 0.42 ms (0.25x) |

## Concurrency

500 C# files, 24 workers — the shape of the review window, which starts a lane per visible file. TextMate serializes every surface through one lock; tree-sitter takes a parser per worker from its pool.

| Engine | 1 thread | 24 threads | Scaling |
| --- | --- | --- | --- |
| TextMate | 7069 ms | 7197 ms | 0.98x |
| tree-sitter | 641 ms | 80 ms | 8.00x |

## Files nobody highlights today

`SyntaxHighlighter.MaxFileChars` is 256 KB; `TreeSitterSyntaxHighlighter.MaxFileBytes` is 1024 KB. Files between the two rendered plain before routing and no longer have to.

- Over 256 KB in this checkout: 13 files in a bundled language.
- Between the two caps (was plain, now colored): 0 files.

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

Per non-whitespace character of every corpus file, both engines reduced to the same `TokenColorSlot` vocabulary.

| Language | TextMate colored | tree-sitter colored | Same slot | Only TextMate | Only tree-sitter | Both, different |
| --- | --- | --- | --- | --- | --- | --- |
| CSharp | 99.9% | 99.8% | 89.6% | 0.2% | 0.0% | 10.2% |
| TypeScript | 93.5% | 98.5% | 82.8% | 1.5% | 6.5% | 9.2% |
| JavaScript | 93.9% | 98.4% | 88.0% | 1.6% | 6.1% | 4.3% |
| Json | 100.0% | 88.1% | 47.8% | 11.9% | 0.0% | 40.2% |
| Css | 93.9% | 92.0% | 59.8% | 2.2% | 0.4% | 31.8% |
| Yaml | 100.0% | 100.0% | 80.9% | 0.0% | 0.0% | 19.1% |
| Python | 78.2% | 88.7% | 64.9% | 11.2% | 21.7% | 2.1% |
| Go | 81.8% | 91.2% | 72.8% | 7.8% | 17.2% | 1.2% |
| Rust | 95.2% | 79.6% | 69.6% | 19.8% | 4.2% | 5.8% |
| Java | 96.3% | 90.4% | 52.5% | 9.6% | 3.7% | 34.2% |
| Bash | 74.1% | 80.3% | 66.6% | 4.5% | 10.7% | 3.0% |
| C | 75.7% | 84.2% | 52.9% | 13.1% | 21.6% | 9.7% |

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
  ts KK PV O V OO V O VP FFFFPVP VPP       CC CCCCCCCCCCC CCC C CCCC CCCCCCCC CCCC
     var list = new Dictionary<string, List<int>>();
  tm KKK VVVV O OOO TTTTTTTTTTPKKKKKKP TTTTPKKKPPPPP
  ts KKK VVVV O KKK TTTTTTTTTTOTTTTTTP TTTTOTTTOOPPP
     var result = obj switch { > 0 and < 10 => "small", _ => nameof(obj) };
  tm KKK VVVVVV O VVV KKKKKK P O N OOO O NN OO SSSSSSSP V OO OOOOOOPVVVP PP
  ts KKK VVVVVV O VVV KKKKKK P O N ... O NN OO SSSSSSSP . OO FFFFFFPVVVP PP
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
  ts KKKK FFF.V TTT. V TTT..VV ..T. V KKKK.T. T. ..T . KKKKKK XXX .
     const raw = `line one
  tm KKKKK VVV O SSSSS SSS
  ts KKKKK VVV O SSSSS SSS
     line two`
  tm SSSS SSSS
  ts SSSS SSSS
     ch := make(chan<- int, 8)
  tm VV OO FFFFPKKKKOO KKKP NP
  ts VV OO FFFF.KKKKOO TTT. N.
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


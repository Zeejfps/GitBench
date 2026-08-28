---
name: xtermsharp-vendoring
description: Vendored XtermSharp must stay netstandard2.0 with our own csproj — NStack.Core's System.Rune and an SDK-rejected AssemblyAttribute are the two reasons
metadata:
  type: project
---

Vendored XtermSharp compiles only as `netstandard2.0`, from a project file we write. It lives in
`vendor/XtermSharp/`, and as of 2026-08-28 it is a **fork**, not a verbatim copy.

**Why:** two independent attempts hit the same two walls. `NStack.Core` declares its own
`System.Rune`, which is unambiguous on netstandard2.0 but collides with `System.Text.Rune` from .NET
Core 3.0 onward — CS0104 at `InputHandlers/InputHandler.cs:1224` and `SelectionService.cs:175`.
And upstream's own `XtermSharp.csproj` does not build under a current SDK: its `InternalsVisibleTo`
`AssemblyAttribute` item carries `Visible="False"` metadata, which newer SDKs pass through as a
second constructor argument.

**How to apply:**
- Put the sources in `vendor/XtermSharp/` and a
  separate `vendor/XtermSharp.Vendored/XtermSharp.Vendored.csproj` that does
  `<Compile Include="..\XtermSharp\**\*.cs" />` with `EnableDefaultCompileItems=false`.
  `NoWarn`: CS0162;CS0168;CS0169;CS0219;CS0414;CS0618;CS0649;CS8981;SYSLIB0011.
- NStack flows transitively, so **every** downstream project needs
  `<Using Include="System.Text.Rune" Alias="Rune" />`. Do not use `PrivateAssets` to hide it — that
  also drops the assembly from the consumer's `deps.json` and the engine then throws
  `FileNotFoundException` while building its first buffer.
- Always set `TerminalOptions.ConvertEol = false`. Its `true` default is console-host behaviour; on
  a pty the line discipline has already produced CRLF and a bare LF means "down one row, same
  column". The default passes most tests and then loses a column where a TUI depends on it.
- Moving these files into `framework/` as net10.0 later means qualifying the two `Rune` call sites
  above to `NStack.Rune`, or putting an extern alias on the reference.

Patching the sources was forbidden until 2026-08-28, when the user decided to maintain the tree as
a fork. Every divergence from the December 2020 clone now goes in `vendor/XtermSharp/PATCHES.md`
with file, change and reason; unpatched gaps stay in `docs/xtermsharp-known-gaps.md` with file and
line. See [[terminal-vt-seam]] and [[xtermsharp-gap-estimates-overcount]].

Build and test only the VT projects — never `GitBench.sln`, which is 30 projects:
`dotnet test GitBench.Terminal.Vt.Tests\GitBench.Terminal.Vt.Tests.csproj --artifacts-path <temp>`.
Add `--logger "trx;LogFileName=x.trx" --results-directory <temp>` and diff the trx per-test
outcomes against a baseline run: the console output truncates the failure list well short of the
~44 that are red, so "nothing else moved" cannot be checked from it.

# Building DiffDino

DiffDino is written in C# using .NET 10. The project and solution names still use
GitBench; the app is named DiffDino.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/downloads)
- A C compiler for the tree-sitter libraries used to understand and highlight code.
  On Windows, install a GNU toolchain such as MSYS2/MinGW and Visual Studio Build
  Tools with C++ support. See the [native build guide](../external/cs_tree_sitter/native/README.md#toolchains)
  for platform details.

## Get the source

```bash
git clone --recurse-submodules https://github.com/Zeejfps/GitBench.git
cd GitBench
```

If you already cloned without submodules:

```bash
git submodule update --init --recursive
```

The framework submodule uses an SSH URL. If you do not have GitHub SSH access,
configure HTTPS for this checkout and try again:

```bash
git config submodule.framework.url https://github.com/Zeejfps/ENV-Game-Framework.git
git submodule update --init --recursive
```

## Build and run

Run these commands from the repository root:

```bash
# Build the native code libraries once per clone. The first run needs internet access.
dotnet run scripts/build-native.cs

dotnet build GitBench.sln
dotnet run --project GitBench/GitBench.csproj
```

Re-run the native build after the pinned versions in
`external/cs_tree_sitter/native/build.cs` change. Its output goes to
`external/cs_tree_sitter/native/artifacts/<rid>/`, where `<rid>` identifies the
platform, such as `win-x64`. Missing libraries cause builds and app tests to fail
at `CheckNativeArtifacts`.

The wrapper also accepts `--clean` and `--target <rid>`:

```bash
dotnet run scripts/build-native.cs -- --target win-x64
```

See the [native build guide](../external/cs_tree_sitter/native/README.md) for compiler
setup and cross-platform build limits.

## Tests

```bash
dotnet test GitBench.Tests/GitBench.Tests.csproj
dotnet test GitBench.Lsp.Tests/GitBench.Lsp.Tests.csproj
dotnet test GitBench.Pty.Tests/GitBench.Pty.Tests.csproj
dotnet test GitBench.Terminal.Vt.Tests/GitBench.Terminal.Vt.Tests.csproj
```

## Development notes

- Read the [coding rules](coding_rules.md) before contributing code.
- Set `DIFFDINO_DATA_DIR` to a temporary folder to run with separate settings and
  repository state. This is useful for testing without changing your usual setup.
- Local builds skip the update check. Auto-update requires an installed build.
- The framework and tree-sitter bindings are Git submodules. Commit changes inside
  a submodule first, then commit its updated reference in this repository.
- The tree-sitter bindings are maintained as part of this project. Changes to its
  C# wrappers or native build support belong in `external/cs_tree_sitter`.

## Releases

The [release workflow](../.github/workflows/release.yml) runs when a version tag such
as `v1.2.3` is pushed. It builds native executables for Windows x64, macOS Apple
Silicon and Intel, and Linux x64, then packages them with Velopack and publishes
installers and update files to GitHub Releases.

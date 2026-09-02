using GitBench.Lsp.Configuration;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Where a server gets launched. Run against a real directory tree rather than a filesystem fake:
/// the walk is path arithmetic plus "does this file exist", and both are things the real one gets
/// wrong in ways an in-memory one cannot reproduce.
/// </summary>
public class ProjectRootTests : IDisposable
{
    static readonly string[] Cargo = ["Cargo.toml"];

    readonly string _base;
    readonly string _repo;

    public ProjectRootTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "gitbench-lsp-root-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_base, "repo");

        // A marker above the repository, of the kind a user's home directory really does have.
        Write(Path.Combine(_base, "Cargo.toml"));

        Write(Path.Combine(_repo, "Cargo.toml"));
        Write(Path.Combine(_repo, "src", "main.rs"));
        Write(Path.Combine(_repo, "scripts", "gen.rs"));
        Write(Path.Combine(_repo, "vendor", "inner", "Cargo.toml"));
        Write(Path.Combine(_repo, "vendor", "inner", "src", "lib.rs"));
        Directory.CreateDirectory(Path.Combine(_repo, "submodule", ".git"));
        Write(Path.Combine(_repo, "submodule", "src", "lib.rs"));
        Write(Path.Combine(_base, "elsewhere", "stray.rs"));
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public void Find_AMarkerBesideTheFile_IsTheRoot()
    {
        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "vendor", "inner", "Cargo.toml"), Cargo);

        Assert.Equal(Path.Combine(_repo, "vendor", "inner"), root);
    }

    [Fact]
    public void Find_AMarkerSeveralLevelsUp_IsTheRoot()
    {
        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "src", "main.rs"), Cargo);

        Assert.Equal(_repo, root);
    }

    [Fact]
    public void Find_ANestedProject_StopsAtTheNearestMarkerRatherThanTheOutermost()
    {
        // The whole reason rootMarkers exists: a crate inside a workspace, or a submodule, is its
        // own project and gets its own server root.
        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "vendor", "inner", "src", "lib.rs"), Cargo);

        Assert.Equal(Path.Combine(_repo, "vendor", "inner"), root);
    }

    [Fact]
    public void Find_AMarkerThatIsADirectory_Counts()
    {
        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "submodule", "src", "lib.rs"), [".git"]);

        Assert.Equal(Path.Combine(_repo, "submodule"), root);
    }

    [Fact]
    public void Find_NoMarkerBetweenTheFileAndTheRepository_FallsBackToTheRepository()
    {
        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "scripts", "gen.rs"), ["go.mod"]);

        Assert.Equal(_repo, root);
    }

    [Fact]
    public void Find_AMarkerAboveTheRepository_IsNeverTheRoot()
    {
        // Walking out of the repository would hand a server the user's home directory as a
        // workspace, which is how an editor eats a machine. The repository has no marker of this
        // kind anywhere, so only the boundary stops the walk.
        Write(Path.Combine(_base, "go.work"));

        var root = ProjectRoot.Find(_repo, Path.Combine(_repo, "scripts", "gen.rs"), ["go.work"]);

        Assert.Equal(_repo, root);
        Assert.NotEqual(_base, root);
    }

    [Fact]
    public void Find_NoMarkersConfigured_IsTheRepositoryItself()
    {
        Assert.Equal(_repo, ProjectRoot.Find(_repo, Path.Combine(_repo, "src", "main.rs"), []));
    }

    [Fact]
    public void Find_AFileOutsideTheRepository_HasNoRoot()
    {
        // A jump into the standard library lands here. There is no project out there to start a
        // server in, so the answer must be "none" rather than a plausible-looking directory.
        Assert.Null(ProjectRoot.Find(_repo, Path.Combine(_base, "elsewhere", "stray.rs"), Cargo));
    }

    [Fact]
    public void Find_AFileInADirectoryNamedLikeTheRepositoryPlusMore_IsOutside()
    {
        var sibling = _repo + "-extra";
        Write(Path.Combine(sibling, "main.rs"));

        Assert.Null(ProjectRoot.Find(_repo, Path.Combine(sibling, "main.rs"), Cargo));
    }

    static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}

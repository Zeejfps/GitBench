using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

public class LanguageRegistryTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("src/app/Main.cs", "csharp")]
    [InlineData("component.ts", "typescript")]
    [InlineData("component.tsx", "typescriptreact")]
    [InlineData("styles.css", "css")]
    [InlineData("App.svelte", "svelte")]
    [InlineData("README.md", "markdown")]
    [InlineData("docs/guide.markdown", "markdown")]
    public void DetectsSupportedExtensions(string path, string expected)
        => Assert.Equal(expected, LanguageRegistry.DetectLanguageId(path));

    [Theory]
    [InlineData("PROGRAM.CS", "csharp")]
    [InlineData("Component.TS", "typescript")]
    [InlineData("Widget.TsX", "typescriptreact")]
    public void IsCaseInsensitive(string path, string expected)
        => Assert.Equal(expected, LanguageRegistry.DetectLanguageId(path));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("Makefile")]      // no extension
    [InlineData("archive.cs.bak")] // extension is .bak, not .cs
    [InlineData("")]
    public void ReturnsNullForUnsupported(string path)
        => Assert.Null(LanguageRegistry.DetectLanguageId(path));

    [Fact]
    public void WindowsStylePathResolvesByExtension()
        => Assert.Equal("csharp", LanguageRegistry.DetectLanguageId(@"C:\repo\src\Foo.cs"));
}

using GitGui;
using Xunit;

namespace GitBench.Tests;

public class LanguageRegistryTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("src/app/Main.cs", "csharp")]
    [InlineData("component.ts", "typescript")]
    [InlineData("component.tsx", "typescriptreact")]
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
    [InlineData("styles.css")]
    [InlineData("README.md")]
    [InlineData("Makefile")]      // no extension
    [InlineData("archive.cs.bak")] // extension is .bak, not .cs
    [InlineData("")]
    public void ReturnsNullForUnsupported(string path)
        => Assert.Null(LanguageRegistry.DetectLanguageId(path));

    [Fact]
    public void WindowsStylePathResolvesByExtension()
        => Assert.Equal("csharp", LanguageRegistry.DetectLanguageId(@"C:\repo\src\Foo.cs"));
}

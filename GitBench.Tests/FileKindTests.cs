using GitBench.Features.FileBrowser;
using Xunit;

namespace GitBench.Tests;

public class FileKindTests
{
    [Fact]
    public void ClassifiesSourceAsCode()
    {
        Assert.Equal(FileKind.Code, FileKinds.Classify("Program.cs"));
        Assert.Equal(FileKind.Code, FileKinds.Classify("build.sh"));
        Assert.Equal(FileKind.Code, FileKinds.Classify("App.razor"));
        Assert.Equal(FileKind.Code, FileKinds.Classify("styles.scss"));
    }

    [Fact]
    public void ClassifiesConfigAndDataAsData()
    {
        Assert.Equal(FileKind.Data, FileKinds.Classify("appsettings.json"));
        Assert.Equal(FileKind.Data, FileKinds.Classify("GitBench.csproj"));
        Assert.Equal(FileKind.Data, FileKinds.Classify("docker-compose.yml"));
    }

    [Fact]
    public void ClassifiesProseAsDocs()
    {
        Assert.Equal(FileKind.Docs, FileKinds.Classify("README.md"));
        Assert.Equal(FileKind.Docs, FileKinds.Classify("spec.pdf"));
    }

    [Fact]
    public void ClassifiesImagesAndSoundAsMedia()
    {
        Assert.Equal(FileKind.Media, FileKinds.Classify("logo.svg"));
        Assert.Equal(FileKind.Media, FileKinds.Classify("clip.mp4"));
    }

    [Fact]
    public void ClassifiesBuildOutputAndArchivesAsBinary()
    {
        Assert.Equal(FileKind.Binary, FileKinds.Classify("GitBench.dll"));
        Assert.Equal(FileKind.Binary, FileKinds.Classify("bundle.tar.gz"));
        Assert.Equal(FileKind.Binary, FileKinds.Classify("Inter.woff2"));
    }

    [Fact]
    public void ExtensionMatchIsCaseInsensitive()
    {
        Assert.Equal(FileKind.Media, FileKinds.Classify("LOGO.PNG"));
        Assert.Equal(FileKind.Code, FileKinds.Classify("Program.CS"));
    }

    [Fact]
    public void RecognizesExtensionlessNamesEveryRepoHas()
    {
        Assert.Equal(FileKind.Code, FileKinds.Classify("Makefile"));
        Assert.Equal(FileKind.Code, FileKinds.Classify("Dockerfile"));
        Assert.Equal(FileKind.Docs, FileKinds.Classify("LICENSE"));
        Assert.Equal(FileKind.Docs, FileKinds.Classify("CHANGELOG"));
        Assert.Equal(FileKind.Other, FileKinds.Classify("notes"));
    }

    [Fact]
    public void TreatsDotfilesAsConfig()
    {
        Assert.Equal(FileKind.Data, FileKinds.Classify(".gitignore"));
        Assert.Equal(FileKind.Data, FileKinds.Classify(".env"));
        Assert.Equal(FileKind.Data, FileKinds.Classify(".whatever"));
        Assert.Equal(FileKind.Data, FileKinds.Classify(".eslintrc.json"));
    }

    [Fact]
    public void UnknownAndDegenerateNamesFallBack()
    {
        Assert.Equal(FileKind.Other, FileKinds.Classify("mystery.qqq"));
        Assert.Equal(FileKind.Other, FileKinds.Classify("weird."));
        Assert.Equal(FileKind.Other, FileKinds.Classify("x." + new string('z', 40)));
        Assert.Equal(FileKind.Other, FileKinds.Classify(new string('n', 40)));
    }
}

using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// A Windows drive letter has three spellings in the wild, and until they were reconciled the
/// difference was load-bearing. typescript-language-server answers with the colon percent-encoded
/// (<c>file:///d%3A/…</c>); this client builds its own uris with it literal (<c>file:///D:/…</c>).
/// <see cref="Uri.LocalPath"/> only recognises a drive when the colon is literal, so an escaped one
/// came back as <c>/d:/Series/main.ts</c> — a path on no platform — and every uri comparison
/// between the two spellings said "different file".
/// </summary>
/// <remarks>
/// Drives exist on one platform, so most of this is about one platform. Elsewhere <c>/d:/x</c> is a
/// directory honestly named <c>d:</c> and must survive untouched, which is what
/// <see cref="ADriveIsOnlyADriveOnWindows"/> holds down.
/// </remarks>
public class DocumentUriDriveTests
{
    [Theory]
    [InlineData("file:///d%3A/Series/repo/main.ts")]
    [InlineData("file:///D%3A/Series/repo/main.ts")]
    [InlineData("file:///d%3a/Series/repo/main.ts")]
    [InlineData("file:///d:/Series/repo/main.ts")]
    [InlineData("file:///D:/Series/repo/main.ts")]
    public void EverySpellingOfADrive_ReadsBackAsTheSameWindowsPath(string uri)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(@"D:\Series\repo\main.ts", DocumentUri.Parse(uri).LocalPath);
    }

    // The comparison that decides whether a wave of diagnostics is about the file on screen. The
    // server's spelling and ours have to land on one value, or every diagnostic is dropped for
    // belonging to a file nobody is looking at.
    [Fact]
    public void AServersSpellingAndOurOwn_AreTheSameUri()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(
            DocumentUri.OfFile(@"D:\Series\repo\main.ts"),
            DocumentUri.Parse("file:///d%3A/Series/repo/main.ts"));
    }

    // Only the drive is rewritten. A name that merely begins like one is a name.
    [Theory]
    [InlineData("file:///dd%3A/Series/main.ts")]
    [InlineData("file:///d%3Aseries/main.ts")]
    public void SomethingThatOnlyLooksLikeADrive_IsLeftAlone(string uri)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(uri, DocumentUri.Parse(uri).Value);
    }

    [Fact]
    public void ADriveIsOnlyADriveOnWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        // A directory named "d:" is a legitimate posix path, and inventing a drive out of it would
        // point the pane at nothing on a system that has none.
        Assert.Equal("/d:/Series/main.ts", DocumentUri.Parse("file:///d%3A/Series/main.ts").LocalPath);
    }

    [Fact]
    public void APathThisClientBuilt_SurvivesTheRoundTrip()
    {
        var path = OperatingSystem.IsWindows() ? @"D:\Series\repo\main.ts" : "/series/repo/main.ts";

        Assert.Equal(path, DocumentUri.OfFile(path).LocalPath);
    }

    [Fact]
    public void AUriThatIsNotAFile_IsLeftAlone()
    {
        const string jar = "jdt://contents/java.base/java.lang/String.class";

        Assert.Equal(string.Empty, DocumentUri.Parse(jar).LocalPath);
        Assert.StartsWith("jdt://", DocumentUri.Parse(jar).Value);
    }
}

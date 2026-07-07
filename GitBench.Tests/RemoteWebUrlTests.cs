using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// RemoteWebUrl turns whatever `git remote get-url` returns into a browser address. The shapes
// below are the ones real hosts hand out (GitHub/GitLab/Bitbucket clone dialogs) plus the
// look-alikes that must NOT open a browser (local paths, file://).
public class RemoteWebUrlTests
{
    [Theory]
    [InlineData("https://github.com/user/repo.git", "https://github.com/user/repo")]
    [InlineData("https://github.com/user/repo", "https://github.com/user/repo")]
    [InlineData("http://git.internal:8080/team/repo.git", "http://git.internal:8080/team/repo")]
    [InlineData("https://user@bitbucket.org/team/repo.git", "https://bitbucket.org/team/repo")]
    [InlineData("https://user:token@gitlab.com/group/repo.git", "https://gitlab.com/group/repo")]
    public void Http_StripsGitSuffixAndCredentials(string remote, string expected)
        => Assert.Equal(expected, RemoteWebUrl.FromRemoteUrl(remote));

    [Theory]
    [InlineData("git@github.com:user/repo.git", "https://github.com/user/repo")]
    [InlineData("git@bitbucket.org:team/repo.git", "https://bitbucket.org/team/repo")]
    [InlineData("git@gitlab.com:group/sub/repo.git", "https://gitlab.com/group/sub/repo")]
    [InlineData("git@myhost:repo.git", "https://myhost/repo")]
    [InlineData("github.com:user/repo.git", "https://github.com/user/repo")]
    public void ScpLike_BecomesHttps(string remote, string expected)
        => Assert.Equal(expected, RemoteWebUrl.FromRemoteUrl(remote));

    [Theory]
    [InlineData("ssh://git@github.com/user/repo.git", "https://github.com/user/repo")]
    [InlineData("ssh://git@gitlab.com:2222/group/repo.git", "https://gitlab.com/group/repo")]
    [InlineData("git://github.com/user/repo.git", "https://github.com/user/repo")]
    public void SshAndGitSchemes_BecomeHttps_DroppingUserAndPort(string remote, string expected)
        => Assert.Equal(expected, RemoteWebUrl.FromRemoteUrl(remote));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:/repos/project")]
    [InlineData(@"C:\repos\project")]
    [InlineData("/srv/git/project.git")]
    [InlineData("../relative/repo")]
    [InlineData("file:///srv/git/project.git")]
    public void LocalPaths_HaveNoWebUrl(string remote)
        => Assert.Null(RemoteWebUrl.FromRemoteUrl(remote));
}

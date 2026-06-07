using Xunit;

namespace GitBench.Tests;

public class IdentityProfileStoreTests
{
    [Fact]
    public void RoundTripsProfilesThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gb-profiles-{Guid.NewGuid():N}.json");
        try
        {
            var original = new List<IdentityProfile>
            {
                new(Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
                    SshKeyPath: "~/.ssh/id_work",
                    Match: new List<IdentityMatchRule> { new("github.com", "series-ai") }),
                new(Guid.NewGuid(), "Personal", "Me", "me@home.com",
                    Match: new List<IdentityMatchRule> { new("github.com") }),
            };

            IdentityProfileStore.Save(path, original);
            var loaded = IdentityProfileStore.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(original[0].Id, loaded[0].Id);
            Assert.Equal("dev@series.ai", loaded[0].UserEmail);
            Assert.Equal("~/.ssh/id_work", loaded[0].SshKeyPath);
            Assert.Equal("series-ai", loaded[0].Match![0].Owner);
            Assert.Null(loaded[1].Match![0].Owner);
            Assert.Null(loaded[1].SshKeyPath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gb-missing-{Guid.NewGuid():N}.json");
        Assert.Empty(IdentityProfileStore.Load(path));
    }
}

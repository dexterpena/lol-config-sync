using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Tests.Lcu;

public class LockfileInfoTests
{
    [Fact]
    public void Parses_lockfile()
    {
        Assert.True(LockfileInfo.TryParse("LeagueClient:1234:54321:abcPW:https", out var info));
        Assert.Equal(new LockfileInfo(1234, 54321, "abcPW", "https"), info);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b:c")]
    [InlineData("LeagueClient:x:54321:pw:https")]
    [InlineData("LeagueClient:1234:notaport:pw:https")]
    public void Rejects_bad_lockfile(string content) => Assert.False(LockfileInfo.TryParse(content, out _));
}

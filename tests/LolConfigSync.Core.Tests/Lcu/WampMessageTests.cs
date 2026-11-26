using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Tests.Lcu;

public class WampMessageTests
{
    [Fact]
    public void Parse_event_frame()
    {
        var e = WampMessage.Parse("""[8,"OnJsonApiEvent",{"data":{"a":1},"eventType":"Update","uri":"/lol-gameflow/v1/gameflow-phase"}]""");

        Assert.NotNull(e);
        Assert.Equal("/lol-gameflow/v1/gameflow-phase", e.Uri);
        Assert.Equal("Update", e.EventType);
        Assert.Equal(1, e.Data!["a"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""[0,"x",1,"y"]""")]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("""[8,"OnJsonApiEvent",{"eventType":"Update"}]""")]
    public void Parse_ignores_other_frames(string text) => Assert.Null(WampMessage.Parse(text));

    [Fact]
    public void Subscribe_frame_matches_protocol() =>
        Assert.Equal("""[5,"OnJsonApiEvent"]""", WampMessage.SubscribeAll);
}

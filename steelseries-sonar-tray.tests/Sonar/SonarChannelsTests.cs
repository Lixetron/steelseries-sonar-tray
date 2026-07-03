using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class SonarChannelsTests
{
    [Theory]
    [InlineData("master", true)]
    [InlineData("GAME", true)]
    [InlineData("chatRender", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void IsValidChannel_recognizes_known_channels(string? channel, bool expected)
    {
        Assert.Equal(expected, SonarChannels.IsValidChannel(channel));
    }

    [Theory]
    [InlineData("GAME", "game")]
    [InlineData("unknown", "master")]
    [InlineData(null, "master")]
    public void NormalizeChannel_falls_back_to_master(string? channel, string expected)
    {
        Assert.Equal(expected, SonarChannels.NormalizeChannel(channel));
    }

    [Theory]
    [InlineData("master", "Master")]
    [InlineData("chatrender", "Chat")]
    [InlineData("aux", "Aux")]
    public void GetDisplayName_maps_known_channels(string channel, string expected)
    {
        Assert.Equal(expected, SonarChannels.GetDisplayName(channel));
    }
}

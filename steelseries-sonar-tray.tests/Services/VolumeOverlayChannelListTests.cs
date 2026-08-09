using SonarQuickMixer.Services;

namespace SonarQuickMixer.Tests.Services;

public class VolumeOverlayChannelListTests
{
    [Fact]
    public void Upsert_AddsChannelsInFirstAppearanceOrder_AndUpdatesInPlace()
    {
        var list = new VolumeOverlayChannelList();

        list.Upsert(new VolumeNotificationState("game", 0.2f, IsMuted: false));
        list.Upsert(new VolumeNotificationState("media", 0.5f, IsMuted: false));
        list.Upsert(new VolumeNotificationState("game", 0.8f, IsMuted: false));
        list.Upsert(new VolumeNotificationState("chatRender", 0.1f, IsMuted: true));

        Assert.Equal(3, list.Count);
        Assert.Equal("game", list.Channels[0].ChannelId);
        Assert.Equal(0.8f, list.Channels[0].Volume, precision: 5);
        Assert.False(list.Channels[0].IsMuted);

        Assert.Equal("media", list.Channels[1].ChannelId);
        Assert.Equal(0.5f, list.Channels[1].Volume, precision: 5);

        Assert.Equal("chatRender", list.Channels[2].ChannelId);
        Assert.True(list.Channels[2].IsMuted);
    }

    [Fact]
    public void Upsert_NormalizesChannelIdCase()
    {
        var list = new VolumeOverlayChannelList();
        list.Upsert(new VolumeNotificationState("Game", 0.3f, false));
        list.Upsert(new VolumeNotificationState("GAME", 0.6f, false));

        Assert.Single(list.Channels);
        Assert.Equal("game", list.Channels[0].ChannelId);
        Assert.Equal(0.6f, list.Channels[0].Volume, precision: 5);
    }

    [Fact]
    public void Clear_RemovesAllChannels()
    {
        var list = new VolumeOverlayChannelList();
        list.Upsert(new VolumeNotificationState("master", 1f, false));
        list.Upsert(new VolumeNotificationState("aux", 0.2f, false));
        list.Clear();

        Assert.Equal(0, list.Count);
        Assert.Empty(list.Channels);
    }

    [Fact]
    public void Snapshot_IsIndependentCopy()
    {
        var list = new VolumeOverlayChannelList();
        list.Upsert(new VolumeNotificationState("media", 0.4f, false));
        var snap = list.Snapshot();
        list.Upsert(new VolumeNotificationState("game", 0.1f, false));

        Assert.Single(snap);
        Assert.Equal(2, list.Count);
    }
}

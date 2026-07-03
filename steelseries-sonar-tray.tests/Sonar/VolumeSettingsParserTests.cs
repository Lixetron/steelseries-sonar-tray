using System.Text.Json;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class VolumeSettingsParserTests
{
    [Fact]
    public void ParseEnabledChannels_adds_master_and_active_devices()
    {
        using var document = JsonDocument.Parse("""
            {
              "masters": {},
              "devices": {
                "game": { "isEnabled": true },
                "media": { "enabled": false },
                "chatRender": {}
              }
            }
            """);

        var enabled = VolumeSettingsParser.ParseEnabledChannels(document.RootElement);

        Assert.Contains("master", enabled);
        Assert.Contains("game", enabled);
        Assert.Contains("chatRender", enabled);
        Assert.DoesNotContain("media", enabled);
    }

    [Fact]
    public void ParseSettingsResponse_classic_reads_volume_and_mute()
    {
        using var document = JsonDocument.Parse("""
            {
              "masters": {
                "classic": { "volume": 50.0, "muted": false }
              },
              "devices": {
                "game": {
                  "classic": { "volume": 80.0, "isMuted": true }
                }
              }
            }
            """);

        var settings = VolumeSettingsParser.ParseSettingsResponse(document.RootElement, streamerMode: false);

        Assert.Equal(50f, settings["master"].Monitoring!.Volume);
        Assert.False(settings["master"].Monitoring!.Muted);
        Assert.Equal(80f, settings["game"].Monitoring!.Volume);
        Assert.True(settings["game"].Monitoring!.Muted);
        Assert.Null(settings["game"].Streaming);
    }

    [Fact]
    public void ParseSettingsResponse_streamer_reads_monitoring_and_streaming()
    {
        using var document = JsonDocument.Parse("""
            {
              "masters": {
                "stream": {
                  "monitoring": { "volume": 40.0 },
                  "streaming": { "volume": 30.0, "muted": true }
                }
              },
              "devices": {
                "game": {
                  "stream": {
                    "monitoring": { "volume": 70.0 },
                    "streaming": { "volume": 60.0 }
                  }
                }
              }
            }
            """);

        var settings = VolumeSettingsParser.ParseSettingsResponse(document.RootElement, streamerMode: true);

        Assert.Equal(40f, settings["master"].Monitoring!.Volume);
        Assert.Equal(30f, settings["master"].Streaming!.Volume);
        Assert.True(settings["master"].Streaming!.Muted);
        Assert.Equal(70f, settings["game"].Monitoring!.Volume);
        Assert.Equal(60f, settings["game"].Streaming!.Volume);
    }

    [Fact]
    public void MergeStreamMixRouting_applies_mix_included_flags()
    {
        using var document = JsonDocument.Parse("""
            {
              "devices": {
                "game": { "classic": { "volume": 50.0 } }
              }
            }
            """);
        var channels = VolumeSettingsParser.ParseSettingsResponse(document.RootElement, streamerMode: false);

        var routing = new StreamMixRouting();
        routing.Monitoring["game"] = true;
        routing.Streaming["game"] = false;

        var merged = VolumeSettingsParser.MergeStreamMixRouting(channels, routing);

        Assert.True(merged["game"].Monitoring!.MixIncluded);
        Assert.False(merged["game"].Streaming!.MixIncluded);
    }

    [Fact]
    public void CreateEmptySnapshot_contains_all_channels()
    {
        var snapshot = VolumeSettingsParser.CreateEmptySnapshot();

        Assert.False(snapshot.IsStreamerMode);
        Assert.Empty(snapshot.EnabledChannels);
        Assert.Equal(SonarChannels.All.Length, snapshot.Channels.Count);
    }
}

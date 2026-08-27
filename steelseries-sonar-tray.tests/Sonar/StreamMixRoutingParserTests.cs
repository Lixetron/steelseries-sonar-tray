using System.Text.Json;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class StreamMixRoutingParserTests
{
    [Fact]
    public void Parse_reads_monitoring_and_streaming_roles()
    {
        using var document = JsonDocument.Parse("""
            [
              {
                "streamRedirectionId": "monitoring",
                "status": [
                  { "role": "game", "isEnabled": true },
                  { "role": "media", "isEnabled": false }
                ]
              },
              {
                "streamRedirectionId": "streaming",
                "status": [
                  { "role": "game", "isEnabled": false }
                ]
              }
            ]
            """);

        var routing = StreamMixRoutingParser.Parse(document.RootElement);

        Assert.True(routing.Monitoring["game"]);
        Assert.False(routing.Monitoring["media"]);
        Assert.False(routing.Streaming["game"]);
    }

    [Fact]
    public void TryGetRedirectionRoleEnabled_finds_matching_entry()
    {
        using var document = JsonDocument.Parse("""
            [
              {
                "streamRedirectionId": "monitoring",
                "status": [
                  { "role": "chatRender", "isEnabled": true }
                ]
              }
            ]
            """);

        var enabled = StreamMixRoutingParser.TryGetRedirectionRoleEnabled(
            document.RootElement,
            SonarEndpoints.StreamRedirectionMonitoringId,
            "chatRender");

        Assert.True(enabled);
    }

    [Fact]
    public void TryReadRedirectionDeviceId_returns_device_for_redirection()
    {
        using var document = JsonDocument.Parse("""
            [
              {
                "streamRedirectionId": "streaming",
                "deviceId": "device-123"
              }
            ]
            """);

        var deviceId = StreamMixRoutingParser.TryReadRedirectionDeviceId(
            document.RootElement,
            SonarEndpoints.StreamRedirectionStreamingId);

        Assert.Equal("device-123", deviceId);
    }

    [Fact]
    public void TryReadClassicRedirectionDeviceId_reads_array_format()
    {
        using var document = JsonDocument.Parse("""
            [
              { "id": "game", "deviceId": "out-1", "isRunning": true },
              { "id": "mic", "deviceId": "mic-1", "isRunning": true },
              { "id": "media", "deviceId": "", "isRunning": false }
            ]
            """);

        Assert.Equal(
            "out-1",
            StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, "game"));
        Assert.Equal(
            "mic-1",
            StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, "mic"));
        Assert.Null(
            StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, "media"));
    }

    [Fact]
    public void TryReadClassicRedirectionDeviceId_reads_object_format()
    {
        using var document = JsonDocument.Parse("""
            {
              "game": { "deviceId": "out-2" },
              "mic": { "deviceId": "mic-2" }
            }
            """);

        Assert.Equal(
            "out-2",
            StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, "game"));
        Assert.Equal(
            "mic-2",
            StreamMixRoutingParser.TryReadClassicRedirectionDeviceId(document.RootElement, "mic"));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void WithMixIncluded_preserves_existing_state(bool? mixIncluded, bool hasState, bool? expectedMix)
    {
        var state = hasState
            ? new SonarChannelState { Volume = 42f, Muted = false }
            : null;

        var updated = StreamMixRoutingParser.WithMixIncluded(state, mixIncluded);

        if (mixIncluded is null)
        {
            Assert.Same(state, updated);
            return;
        }

        Assert.Equal(expectedMix, updated!.MixIncluded);
        if (hasState)
        {
            Assert.Equal(42f, updated.Volume);
            Assert.False(updated.Muted);
        }
    }
}

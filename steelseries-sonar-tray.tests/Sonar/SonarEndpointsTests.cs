using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class SonarEndpointsTests
{
    private const string BaseUrl = "http://127.0.0.1:12345";

    [Theory]
    [InlineData(false, "/volumeSettings/classic")]
    [InlineData(true, "/volumeSettings/streamer")]
    public void VolumeSettingsPath_switches_by_mode(bool streamerMode, string expectedSuffix)
    {
        Assert.Equal(expectedSuffix, SonarEndpoints.VolumeSettingsPath(streamerMode));
    }

    [Fact]
    public void SetVolume_classic_builds_expected_url()
    {
        var url = SonarEndpoints.SetVolume(BaseUrl, "game", "0.75", streamerMode: false, SonarMixerPath.Monitoring);

        Assert.Equal($"{BaseUrl}/volumeSettings/classic/game/Volume/0.75", url);
    }

    [Fact]
    public void SetVolume_streamer_monitoring_builds_expected_url()
    {
        var url = SonarEndpoints.SetVolume(BaseUrl, "game", "0.75", streamerMode: true, SonarMixerPath.Monitoring);

        Assert.Equal($"{BaseUrl}/volumeSettings/streamer/monitoring/game/Volume/0.75", url);
    }

    [Fact]
    public void SetMute_streamer_streaming_builds_expected_url()
    {
        var url = SonarEndpoints.SetMute(BaseUrl, "media", "true", streamerMode: true, SonarMixerPath.Streaming);

        Assert.Equal($"{BaseUrl}/volumeSettings/streamer/streaming/media/isMuted/true", url);
    }

    [Fact]
    public void SetMixIncluded_builds_redirection_url()
    {
        var url = SonarEndpoints.SetMixIncluded(BaseUrl, "game", included: true, SonarMixerPath.Monitoring);

        Assert.Equal($"{BaseUrl}/streamRedirections/monitoring/redirections/game/isEnabled/true", url);
    }

    [Fact]
    public void AudioDevices_builds_expected_url()
    {
        Assert.Equal($"{BaseUrl}/audioDevices", SonarEndpoints.AudioDevices(BaseUrl));
    }

    [Fact]
    public void SetClassicRedirectionDevice_url_encodes_device_id()
    {
        var deviceId = "{0.0.0.00000000}.{abc}";
        var url = SonarEndpoints.SetClassicRedirectionDevice(BaseUrl, "render", deviceId);

        Assert.Equal(
            $"{BaseUrl}/classicRedirections/render/deviceId/{Uri.EscapeDataString(deviceId)}",
            url);
    }

    [Fact]
    public void SetStreamRedirectionDevice_builds_mic_url()
    {
        var deviceId = "{0.0.1.00000000}.{mic}";
        var url = SonarEndpoints.SetStreamRedirectionDevice(BaseUrl, "mic", deviceId);

        Assert.Equal(
            $"{BaseUrl}/streamRedirections/mic/deviceId/{Uri.EscapeDataString(deviceId)}",
            url);
    }
}

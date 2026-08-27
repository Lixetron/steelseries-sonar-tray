using System.Text.Json;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class SonarAudioDevicesParserTests
{
    [Fact]
    public void Parse_and_filter_physical_devices_by_flow()
    {
        using var document = JsonDocument.Parse("""
            [
              {
                "friendlyName": "Headphones",
                "id": "render-1",
                "dataFlow": "render",
                "isVad": false,
                "state": "active"
              },
              {
                "friendlyName": "Sonar Virtual",
                "id": "render-vad",
                "dataFlow": "render",
                "isVad": true,
                "state": "active"
              },
              {
                "friendlyName": "Mic",
                "id": "capture-1",
                "dataFlow": "capture",
                "isVad": false,
                "state": "active"
              },
              {
                "friendlyName": "Dead Mic",
                "id": "capture-dead",
                "dataFlow": "capture",
                "isVad": false,
                "state": "disabled"
              }
            ]
            """);

        var devices = SonarAudioDevicesParser.Parse(document.RootElement);
        var outputs = SonarAudioDevicesParser.FilterPhysical(devices, SonarAudioDataFlow.Render);
        var inputs = SonarAudioDevicesParser.FilterPhysical(devices, SonarAudioDataFlow.Capture);

        Assert.Equal(4, devices.Count);
        Assert.Single(outputs);
        Assert.Equal("render-1", outputs[0].Id);
        Assert.Single(inputs);
        Assert.Equal("capture-1", inputs[0].Id);
    }
}

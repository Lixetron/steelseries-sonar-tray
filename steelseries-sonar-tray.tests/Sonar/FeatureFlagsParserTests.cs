using System.Text.Json;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class FeatureFlagsParserTests
{
    [Fact]
    public void ApplyOptionalChannelFlags_adds_and_removes_optional_channels()
    {
        using var document = JsonDocument.Parse("""
            {
              "mediaEnabled": true,
              "nested": {
                "auxChannelEnabled": false
              }
            }
            """);
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aux", "game" };

        FeatureFlagsParser.ApplyOptionalChannelFlags(document.RootElement, enabled);

        Assert.Contains("media", enabled);
        Assert.DoesNotContain("aux", enabled);
        Assert.Contains("game", enabled);
    }
}

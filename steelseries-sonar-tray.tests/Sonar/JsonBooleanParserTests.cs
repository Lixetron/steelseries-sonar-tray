using System.Text.Json;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Tests.Sonar;

public class JsonBooleanParserTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"true\"", true)]
    public void TryParseBooleanLike_parses_common_values(string input, bool expected)
    {
        var success = JsonBooleanParser.TryParseBooleanLike(input, out var result);

        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maybe")]
    public void TryParseBooleanLike_returns_false_for_invalid_input(string input)
    {
        var success = JsonBooleanParser.TryParseBooleanLike(input, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseBooleanElement_reads_nested_enabled_property()
    {
        using var document = JsonDocument.Parse("""{"enabled": {"value": "1"}}""");

        var success = JsonBooleanParser.TryParseBooleanElement(document.RootElement, out var result);

        Assert.True(success);
        Assert.True(result);
    }

    [Fact]
    public void TryFindBooleanProperty_searches_nested_objects()
    {
        using var document = JsonDocument.Parse("""
            {
              "settings": {
                "features": {
                  "isEnabled": false
                }
              }
            }
            """);

        var success = JsonBooleanParser.TryFindBooleanProperty(
            document.RootElement,
            out var enabled,
            "isEnabled");

        Assert.True(success);
        Assert.False(enabled);
    }
}

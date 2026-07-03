using System.Text.Json;

namespace SonarQuickMixer.Sonar;

internal static class JsonBooleanParser
{
    public static bool TryParseBooleanLike(string value, out bool result)
    {
        result = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().Trim('"');
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (int.TryParse(value, out var number))
        {
            result = number != 0;
            return true;
        }

        return false;
    }

    public static bool TryParseBooleanElement(JsonElement element) =>
        TryParseBooleanElement(element, out _);

    public static bool TryParseBooleanElement(JsonElement element, out bool result)
    {
        result = false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return TryParseBooleanLike(element.GetString() ?? string.Empty, out result);
            case JsonValueKind.Number:
                result = element.TryGetInt32(out var number) ? number != 0 : Math.Abs(element.GetDouble()) > 0.0001d;
                return true;
            case JsonValueKind.Object:
                if (element.TryGetProperty("value", out var value) && TryParseBooleanElement(value, out result))
                {
                    return true;
                }

                if (element.TryGetProperty("enabled", out var enabled) && TryParseBooleanElement(enabled, out result))
                {
                    return true;
                }

                if (element.TryGetProperty("isEnabled", out var isEnabled) &&
                    TryParseBooleanElement(isEnabled, out result))
                {
                    return true;
                }

                break;
        }

        return false;
    }

    public static bool TryFindBooleanProperty(JsonElement element, params string[] propertyNames) =>
        TryFindBooleanProperty(element, out _, propertyNames);

    public static bool TryFindBooleanProperty(
        JsonElement element,
        out bool enabled,
        params string[] propertyNames)
    {
        enabled = false;
        return TryFindBooleanProperty(element, propertyNames, depth: 0, out enabled);
    }

    private static bool TryFindBooleanProperty(
        JsonElement element,
        string[] propertyNames,
        int depth,
        out bool enabled)
    {
        enabled = false;

        if (depth > 8)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var flag) &&
                    TryParseBooleanElement(flag, out enabled))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindBooleanProperty(property.Value, propertyNames, depth + 1, out enabled))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindBooleanProperty(child, propertyNames, depth + 1, out enabled))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

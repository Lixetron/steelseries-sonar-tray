using System.Reflection;

namespace SonarQuickMixer;

public static class AppVersion
{
    public static Version Current { get; } = GetCurrentVersion();

    public static string Display => Format(Current);

    public static string Format(Version version) =>
        $"v{version.Major}.{version.Minor}.{version.Build}";

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            var core = informational.Split('+')[0];
            if (Version.TryParse(core, out var parsed))
            {
                return parsed;
            }
        }

        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }
}

using System.Reflection;

namespace DeepFilterNetGui.Services;

internal static class AppVersion
{
    public static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (!string.IsNullOrWhiteSpace(info?.InformationalVersion))
            return info!.InformationalVersion;

        var version = assembly.GetName().Version;
        return version?.ToString() ?? string.Empty;
    }
}


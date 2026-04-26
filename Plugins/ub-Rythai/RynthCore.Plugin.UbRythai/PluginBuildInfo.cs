using System.Reflection;

namespace RynthCore.Plugin.UbRythai;

/// <summary>Build/version strings aligned with the .csproj (net10.0-windows; not net48).</summary>
internal static class PluginBuildInfo
{
    internal static string AssemblyName => typeof(UbRythaiPlugin).Assembly.GetName().Name ?? "RynthCore.Plugin.UbRythai";

    internal static string AssemblyInformationalVersion =>
        typeof(UbRythaiPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UbRythaiPlugin).Assembly.GetName().Version?.ToString()
        ?? "3.1.0-p1-p4-baseline";

    internal static string DisplayVersion => $"v{AssemblyInformationalVersion} net10";
}

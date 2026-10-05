using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Resolves published RynthSuite tool executables (LootEditor, MonsterEditor).
/// The bundle layout places tools under the RynthCore install: <c>{app}\Tools\MonsterEditor\</c> next to
/// <c>RynthCore.exe</c>, while the plugin DLL lives under <c>{app}\Runtime\Plugins\</c> (or a shadow
/// <c>.runtime\</c> path). We locate tools by walking up from this assembly's file path, not from the
/// RynthAi profile folder (which can point at dev or alternate drives) or a repo-relative "Games" parent.
/// </summary>
internal static class SuiteToolPaths
{
    /// <summary>
    /// <paramref name="settingsRoot"/> is typically <c>...\RynthAi\SettingsProfiles\ACEmulator</c>; returns <c>...\RynthAi</c>.
    /// </summary>
    internal static string GetRynthAiRootFromSettingsRoot(string settingsRoot)
    {
        if (string.IsNullOrEmpty(settingsRoot)) return string.Empty;
        try
        {
            return Path.GetDirectoryName(Path.GetDirectoryName(settingsRoot)!)!;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <param name="toolFolder">e.g. <c>MonsterEditor</c> or <c>LootEditor</c>.</param>
    /// <param name="exeName">e.g. <c>RynthCore.MonsterEditor.exe</c>.</param>
    internal static string? FindPublishedTool(string rynthAiRoot, string toolFolder, string exeName)
    {
        // 1) On-disk path of *this* plugin. Walk up to the Rynth install: ...\RynthCore\Runtime\Plugins\... → ...\RynthCore\Tools\...
        //    (Must be first: when injected into acclient, AppContext.BaseDirectory is the game folder, not the Rynth layout.)
        string? fromPlugin = FindToolWalkingUpFromDirectory(GetThisAssemblyPathRoot(), toolFolder, exeName);
        if (!string.IsNullOrEmpty(fromPlugin))
            return fromPlugin;

        // 2) Rynth self-hosted or tests: the host’s base is the Rynth app folder with Tools\ next to it.
        string? fromHost = FindToolWalkingUpFromDirectory(AppContext.BaseDirectory, toolFolder, exeName);
        if (!string.IsNullOrEmpty(fromHost))
            return fromHost;

        // 3) Legacy / dev: paths derived from the RynthAi data folder
        if (string.IsNullOrEmpty(rynthAiRoot)) return null;

        foreach (string candidate in BuildToolCandidatePaths(rynthAiRoot, toolFolder, exeName))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Directory containing the plugin file (e.g. shadow <c>.runtime\…</c>); empty if unknown.</summary>
    // IL3000 warns that Location is empty in single-file apps. This is a separate NativeAOT .dll, not a bundled assembly.
    [UnconditionalSuppressMessage("SingleFile", "IL3000:Assembly.Location.get", Justification = "RynthCore.Plugin.RynthAi is published as a native AOT .dll; Location is the on-disk path under Runtime\\Plugins.")]
    private static string? GetThisAssemblyPathRoot()
    {
        string? loc = typeof(SuiteToolPaths).Assembly.Location;
        return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
    }

    /// <summary>
    /// From <paramref name="startDir"/> (e.g. the host’s base or a shadow-copied plugin folder) walk
    /// toward the volume root; at each level, look for <c>Tools\toolFolder\exeName</c> (RynthBundle layout).
    /// </summary>
    private static string? FindToolWalkingUpFromDirectory(string? startDir, string toolFolder, string exeName)
    {
        if (string.IsNullOrEmpty(startDir)) return null;
        try
        {
            // Normalize so a trailing slash does not break parent walks.
            string? dir = Path.GetFullPath(startDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 24 && !string.IsNullOrEmpty(dir); i++)
            {
                if (!seen.Add(dir)) break;
                string candidate = Path.Combine(dir, "Tools", toolFolder, exeName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }
        }
        catch
        {
            // ignored: invalid paths, IO
        }
        return null;
    }

    private static List<string> BuildToolCandidatePaths(string rynthAiRoot, string toolFolder, string exeName)
    {
        var list = new List<string>
        {
            // Legacy: <RynthAi>\MonsterEditor\*.exe
            Path.Combine(rynthAiRoot, toolFolder, exeName),
            // Bundle: <RynthAi>\Tools\MonsterEditor\*.exe
            Path.Combine(rynthAiRoot, "Tools", toolFolder, exeName),
        };

        // Sibling install: <Games>\RynthCore\Tools\<toolFolder>\*.exe when data is <Games>\RynthSuite\RynthAi
        try
        {
            var rynthAiDir = new DirectoryInfo(rynthAiRoot);
            DirectoryInfo? suiteOrParent = rynthAiDir.Parent;
            DirectoryInfo? games = suiteOrParent?.Parent;
            if (games != null)
                list.Add(Path.Combine(games.FullName, "RynthCore", "Tools", toolFolder, exeName));
        }
        catch
        {
            // ignored
        }

        return list;
    }
}

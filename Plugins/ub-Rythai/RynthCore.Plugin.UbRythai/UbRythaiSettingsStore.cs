using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.UbRythai;

/// <summary>Loads and saves <see cref="UbRythaiSettings"/> per character under Documents\RynthSuite\ub-Rythai\.</summary>
internal static class UbRythaiSettingsStore
{
    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RynthSuite",
        "ub-Rythai");

    /// <summary>Returns the on-disk path for the settings file, or null if <paramref name="charName"/> is empty.</summary>
    public static string? GetSettingsFilePath(string? charName)
    {
        if (string.IsNullOrWhiteSpace(charName))
            return null;

        string safe = SanitizeFileName(charName.Trim());
        string dir = Path.Combine(RootDir, safe);
        return Path.Combine(dir, "UbRythai-settings.json");
    }

    public static UbRythaiSettings LoadOrDefault(string? charName)
    {
        string? path = GetSettingsFilePath(charName);
        if (path == null || !File.Exists(path))
            return new UbRythaiSettings();

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            UbRythaiSettings? loaded = JsonSerializer.Deserialize(json, UbRythaiJsonContext.Default.UbRythaiSettings);
            return loaded ?? new UbRythaiSettings();
        }
        catch
        {
            return new UbRythaiSettings();
        }
    }

    public static void Save(string? charName, UbRythaiSettings settings)
    {
        string? path = GetSettingsFilePath(charName);
        if (path == null)
            return;

        try
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(settings, UbRythaiJsonContext.Default.UbRythaiSettings);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
            // best-effort persistence
        }
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' ? c : '_');
        return sb.ToString();
    }
}

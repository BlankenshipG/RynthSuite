// IltSettings.cs - RynthILT's own settings (every character): which world names are Infinite
// Leaftide. Lives in %APPDATA%\RynthCore\RynthILT\settings.json.
//
// The list was RynthAi's Settings > Misc "Server Features" (FeatureServerNames, saved with each
// RynthAi profile). RynthILT keeps one list for every character; the first time a character
// logs in with RynthILT, IltMigration folds that character's RynthAi list into this one.
// Edited with "/ilt servers <list>" (the Character tab's editor went with the tab, 2026-10-13;
// RynthCore Options > Diagnostics lists the world names RynthCore uses).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

public sealed class IltSettings
{
    public int SchemaVersion = 1;

    /// <summary>World-name patterns that count as Infinite Leaftide ('*' wildcard, case-insensitive).</summary>
    public List<string> ServerNames = new(ServerFeatureGate.DefaultServerNames);

    /// <summary>The shared RynthAi files (Hub profiles, aug costs) were copied over once.</summary>
    public bool SharedFilesMigrated;

    /// <summary>
    /// RynthILT character folders whose copy from RynthAi failed part-way (IltMigration). Until it
    /// succeeds the Hub writes nothing there (a fresh file would shadow the RynthAi original and
    /// stop it ever being copied); the copy is retried at each login and every few minutes.
    /// </summary>
    public List<string> PendingCharacterMigrations = new();
}

/// <summary>Loads and saves <see cref="IltSettings"/>; a broken or missing file reads as the defaults.</summary>
internal static class IltSettingsStore
{
    public const string FileName = "settings.json";

    /// <summary>Overridable for the tests.</summary>
    public static string Folder = RynthILTPlugin.SettingsRoot;

    public static string FilePath => Path.Combine(Folder, FileName);

    public static IltSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), IltHubJsonContext.Default.IltSettings);
                if (s != null)
                {
                    s.ServerNames = ServerFeatureGate.ParseList(string.Join(",", s.ServerNames ?? new List<string>()));
                    if (s.ServerNames.Count == 0) s.ServerNames = new List<string>(ServerFeatureGate.DefaultServerNames);
                    return s;
                }
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, $"load {FilePath}"); }
        return new IltSettings();
    }

    public static void Save(IltSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, IltHubJsonContext.Default.IltSettings);
            DataWriteGuard.TrySave("RynthILT's settings", FilePath, () =>
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            });
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.IltStore, ex, $"save {FilePath}"); }
    }
}

// Source-generated System.Text.Json only (NativeAOT; never reflection-serialize plugin types:
// the reflection cache keeps the old plugin generation alive across a hot reload).
[JsonSerializable(typeof(IltSettings))]
[JsonSerializable(typeof(IltHubState))]
[JsonSerializable(typeof(List<IltBankTransaction>), TypeInfoPropertyName = "IltBankTransactionList")]
[JsonSerializable(typeof(IltAugCostFile))]
[JsonSerializable(typeof(IltLegacyProfileFlags))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNameCaseInsensitive = true)]
internal partial class IltHubJsonContext : JsonSerializerContext { }

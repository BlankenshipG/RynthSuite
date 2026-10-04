using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>
/// RynthOracle's settings, in %APPDATA%\RynthCore\rynthoracle.json. Written by hand and read
/// with JsonDocument (no reflection serializer: plugin types must never go through
/// System.Text.Json's serializer, which roots them across hot reloads).
/// </summary>
internal sealed class OracleSettings
{
    /// <summary>Send /myquests once after login (its reply hidden) so the quest tabs have data.</summary>
    public bool ReadQuestsAtLogin = true;
    /// <summary>Hide the server's reply to RynthOracle's own /myquests (a typed /myquests always shows).</summary>
    public bool HideOwnQuestReplies = true;
    /// <summary>Check once a day for a newer master quest list at upstream's address.</summary>
    public bool CheckQuestListDaily;
    public DateTime LastQuestListCheckUtc = DateTime.MinValue;
    /// <summary>Show the Void target window by itself for characters with Void Magic.</summary>
    public bool AutoShowVoidWindow = true;
    public bool MainWindowOpen;
    /// <summary>The selected tab, by name (the set of tabs depends on the server).</summary>
    public string TabName = "Quests";
    /// <summary>ConquestAC: "/bank deposit" at every ten-minute mark (upstream's auto-deposit; off by default).</summary>
    public bool ConquestAutoDeposit;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthoracle.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            ReadQuestsAtLogin = Bool(r, "readQuestsAtLogin", ReadQuestsAtLogin);
            HideOwnQuestReplies = Bool(r, "hideOwnQuestReplies", HideOwnQuestReplies);
            CheckQuestListDaily = Bool(r, "checkQuestListDaily", CheckQuestListDaily);
            AutoShowVoidWindow = Bool(r, "autoShowVoidWindow", AutoShowVoidWindow);
            MainWindowOpen = Bool(r, "mainWindowOpen", MainWindowOpen);
            ConquestAutoDeposit = Bool(r, "conquestAutoDeposit", ConquestAutoDeposit);
            if (r.TryGetProperty("tabName", out JsonElement t) && t.ValueKind == JsonValueKind.String) TabName = t.GetString() ?? TabName;
            if (r.TryGetProperty("lastQuestListCheck", out JsonElement c) && c.ValueKind == JsonValueKind.String
                && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when))
                LastQuestListCheckUtc = when.ToUniversalTime();
        }
        catch
        {
            // A broken file means defaults; the next save rewrites it.
        }
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"readQuestsAtLogin\": ").Append(ReadQuestsAtLogin ? "true" : "false").Append(",\n");
            sb.Append("  \"hideOwnQuestReplies\": ").Append(HideOwnQuestReplies ? "true" : "false").Append(",\n");
            sb.Append("  \"checkQuestListDaily\": ").Append(CheckQuestListDaily ? "true" : "false").Append(",\n");
            sb.Append("  \"lastQuestListCheck\": \"").Append(LastQuestListCheckUtc.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"autoShowVoidWindow\": ").Append(AutoShowVoidWindow ? "true" : "false").Append(",\n");
            sb.Append("  \"mainWindowOpen\": ").Append(MainWindowOpen ? "true" : "false").Append(",\n");
            sb.Append("  \"conquestAutoDeposit\": ").Append(ConquestAutoDeposit ? "true" : "false").Append(",\n");
            sb.Append("  \"tabName\": \"").Append(JsonEncodedText.Encode(TabName).ToString()).Append("\"\n");
            sb.Append("}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; a failed write keeps the old file.
        }
    }

    private static bool Bool(JsonElement r, string name, bool fallback) =>
        r.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
}

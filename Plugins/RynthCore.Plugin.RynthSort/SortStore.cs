using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthCore.Plugin.RynthSort;

/// <summary>RynthSort's settings for one character (the Lua script's DEFAULTS, plus what the plugin adds).</summary>
internal sealed class SortSettings
{
    public const int SettleMin = 0, SettleMax = 30, GapMin = 100, GapMax = 3000;

    /// <summary>Auto-sort: new items in the main pack go to their pack after the settle time.</summary>
    public bool Auto = true;
    /// <summary>How long a new item sits in the main pack before it moves (seconds).</summary>
    public int SettleSec = 5;
    /// <summary>Auto-sort's pause between two moves (ms), to leave the bot room.</summary>
    public int GapMs = 300;
    /// <summary>Moves may go while RynthAi fights (never while it waits on the server or animates).</summary>
    public bool SortInCombat;
    /// <summary>The window was open when last seen.</summary>
    public bool WindowOpen = true;

    public void Clamp()
    {
        SettleSec = Math.Clamp(SettleSec, SettleMin, SettleMax);
        GapMs = Math.Clamp(GapMs, GapMin, GapMax);
    }
}

/// <summary>One character's rules and settings.</summary>
internal sealed class CharacterSortData
{
    public List<SortRule> Rules = SortRules.Defaults();
    public SortSettings Settings = new();
    /// <summary>Where these came from on first run ("" = defaults, else the Lua script's file).</summary>
    public string ImportedFrom = string.Empty;
}

/// <summary>
/// Per-character files: &lt;SuiteDir&gt;\RynthSort\characters\&lt;server_character&gt;.json, written by
/// hand and read with JsonDocument (plugin types never go through System.Text.Json's serializer,
/// which roots them across hot reloads). The same shape as the Lua script's saved data
/// ({"rules":[...],"settings":{...}}), so its file (&lt;SuiteDir&gt;\RynthLua\Data\RynthSort\characters\...)
/// is read the same way when a character has no RynthSort file yet.
/// </summary>
internal sealed class SortStore
{
    public const int MaxRules = 64;
    public const int MaxPack = 32;

    /// <summary>RynthSort's folder (tests point it at a temp folder).</summary>
    public string Root;
    /// <summary>The Lua script's data folder for RynthSort (tests point it at a temp folder).</summary>
    public string LuaDataRoot;

    public SortStore()
    {
        string suite = RynthCore.Install.RynthInstallPaths.SuiteDir;
        Root = Path.Combine(suite, "RynthSort");
        LuaDataRoot = Path.Combine(suite, "RynthLua", "Data", "RynthSort");
    }

    public SortStore(string root, string luaDataRoot)
    {
        Root = root;
        LuaDataRoot = luaDataRoot;
    }

    /// <summary>"server_character" made safe as a file name, as the Lua script's storage names it.</summary>
    public static string FileStem(string server, string character) => SafeFileName($"{server}_{character}");

    public string PathFor(string server, string character) => Path.Combine(Root, "characters", FileStem(server, character) + ".json");
    public string LuaPathFor(string server, string character) => Path.Combine(LuaDataRoot, "characters", FileStem(server, character) + ".json");

    /// <summary>
    /// The character's rules and settings: RynthSort's own file; else the Lua script's (imported:
    /// <see cref="CharacterSortData.ImportedFrom"/> says so, and the caller saves it as RynthSort's);
    /// else the defaults. A broken file reads as the defaults (the next save rewrites it).
    /// </summary>
    public CharacterSortData Load(string server, string character, out string? problem)
    {
        problem = null;
        string own = PathFor(server, character);
        if (File.Exists(own))
        {
            if (TryRead(own, out CharacterSortData? d, out problem)) return d!;
            return new CharacterSortData();
        }
        string lua = LuaPathFor(server, character);
        if (File.Exists(lua) && TryRead(lua, out CharacterSortData? imported, out problem))
        {
            imported!.ImportedFrom = lua;
            return imported;
        }
        return new CharacterSortData();
    }

    public static bool TryRead(string path, out CharacterSortData? data, out string? problem)
    {
        data = null;
        problem = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            data = Parse(doc.RootElement);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = $"{Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Rules and settings from the file's root object; anything missing or odd keeps its default.</summary>
    internal static CharacterSortData Parse(JsonElement root)
    {
        var d = new CharacterSortData();
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file isn't a JSON object");

        if (root.TryGetProperty("rules", out JsonElement rules))
        {
            // The Lua script saved an empty rule list as {} (an empty Lua table): no rules either.
            if (rules.ValueKind == JsonValueKind.Array)
            {
                d.Rules = new List<SortRule>();
                foreach (JsonElement e in rules.EnumerateArray())
                {
                    if (d.Rules.Count >= MaxRules) break;
                    SortRule? r = ParseRule(e);
                    if (r != null) d.Rules.Add(r);
                }
            }
            else if (rules.ValueKind == JsonValueKind.Object && !rules.EnumerateObject().MoveNext())
            {
                d.Rules = new List<SortRule>();
            }
        }

        if (root.TryGetProperty("settings", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
        {
            SortSettings st = d.Settings;
            st.Auto = Bool(s, "auto", st.Auto);
            st.SettleSec = Int(s, "settleSec", st.SettleSec);
            st.GapMs = Int(s, "gapMs", st.GapMs);
            st.SortInCombat = Bool(s, "sortInCombat", st.SortInCombat);
            st.WindowOpen = Bool(s, "windowOpen", st.WindowOpen);
            st.Clamp();
        }
        if (root.TryGetProperty("importedFrom", out JsonElement f) && f.ValueKind == JsonValueKind.String)
            d.ImportedFrom = f.GetString() ?? string.Empty;
        return d;
    }

    private static SortRule? ParseRule(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var r = new SortRule
        {
            On = Bool(e, "on", true),
            Kind = e.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String
                   && string.Equals(k.GetString(), "name", StringComparison.OrdinalIgnoreCase) ? RuleKind.Name : RuleKind.Class,
            Class = Int(e, "class", ObjectClasses.SpellComponent),
            Text = e.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty,
            Pack = Math.Clamp(Int(e, "pack", 1), 1, MaxPack),
        };
        if (r.Text.Length > 64) r.Text = r.Text[..64];
        return r;
    }

    /// <summary>Writes the character's file (temp file, then moved over the old one). Returns an error or null.</summary>
    public string? Save(string server, string character, CharacterSortData d)
    {
        string path = PathFor(server, character);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, ToJson(d), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal static string ToJson(CharacterSortData d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("rules");
            foreach (SortRule r in d.Rules)
            {
                w.WriteStartObject();
                w.WriteBoolean("on", r.On);
                w.WriteString("kind", r.Kind == RuleKind.Name ? "name" : "class");
                w.WriteNumber("class", r.Class);
                w.WriteString("text", r.Text ?? string.Empty);
                w.WriteNumber("pack", r.Pack);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("settings");
            w.WriteBoolean("auto", d.Settings.Auto);
            w.WriteNumber("settleSec", d.Settings.SettleSec);
            w.WriteNumber("gapMs", d.Settings.GapMs);
            w.WriteBoolean("sortInCombat", d.Settings.SortInCombat);
            w.WriteBoolean("windowOpen", d.Settings.WindowOpen);
            w.WriteEndObject();
            if (d.ImportedFrom.Length > 0) w.WriteString("importedFrom", d.ImportedFrom);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static bool Bool(JsonElement o, string name, bool fallback) =>
        o.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    private static int Int(JsonElement o, string name, int fallback)
    {
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return fallback;
        if (v.TryGetInt32(out int i)) return i;
        // Lua numbers are doubles: 5.0 is 5.
        return v.TryGetDouble(out double dbl) && dbl >= int.MinValue && dbl <= int.MaxValue ? (int)Math.Round(dbl) : fallback;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>RynthLua's LibraryStorage.SafeFileName, so both name a character's file the same way.</summary>
    public static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? string.Empty).Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] < 32) chars[i] = '_';
        string n = new string(chars).TrimEnd('.', ' ');
        if (n.Length > 80) n = n[..80].TrimEnd('.', ' ');
        if (n.Length == 0 || n.Trim('.').Length == 0) n = "_" + n;
        string stem = n.Split('.')[0];
        if (ReservedNames.Contains(stem)) n = "_" + n;
        return n;
    }
}

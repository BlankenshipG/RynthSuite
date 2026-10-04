// IltHubStore.cs — Disk persistence for the ILT Hub.
//
// Files (all under the per-character settings folder the dashboard already uses):
//   <charFolder>\ilt-hub.json               live Hub state (IltHubState)
//   <charFolder>\ilt-hub-transactions.json  bank transaction log (capped)
//   <charFolder>\ilt-hub-profiles\<n>.json  named per-character Hub profiles
//   <root>\IltHubProfiles\<n>.json          named profiles shared by every character
// Saves are dirty-checked (the serialized JSON is compared to what was last written) so
// the periodic save in Tick costs nothing when nothing changed. A one-time import pulls
// UtilityBelt's bankdata sidecars so players keep their balances and history.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltHubStore
{
    /// <summary>Maximum bank transactions kept on disk (oldest dropped first).</summary>
    public const int MaxTransactions = 1000;

    /// <summary>Shared root (C:\Games\RynthSuite\RynthAi) for profiles usable by every character.</summary>
    public const string SharedRoot = @"C:\Games\RynthSuite\RynthAi";

    private readonly RynthCoreHost _host;
    private readonly string _charFolder;
    private string _lastStateJson = string.Empty;
    private string _lastTxJson = string.Empty;

    public IltHubStore(RynthCoreHost host, string charFolder)
    {
        _host = host;
        _charFolder = charFolder ?? string.Empty;
    }

    public string CharFolder => _charFolder;
    private string StatePath => Path.Combine(_charFolder, "ilt-hub.json");
    private string TxPath => Path.Combine(_charFolder, "ilt-hub-transactions.json");
    private string CharProfileDir => Path.Combine(_charFolder, "ilt-hub-profiles");
    private static string SharedProfileDir => Path.Combine(SharedRoot, "IltHubProfiles");

    // ── State ───────────────────────────────────────────────────────────────

    /// <summary>Loads the live state, returning defaults when the file is missing or unreadable.</summary>
    public IltHubState LoadState()
    {
        try
        {
            if (_charFolder.Length > 0 && File.Exists(StatePath))
            {
                string json = File.ReadAllText(StatePath);
                var s = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.IltHubState);
                if (s != null)
                {
                    Normalize(s);
                    _lastStateJson = json;
                    return s;
                }
            }
        }
        catch (Exception ex) { _host.Log($"[IltHub] load {StatePath} failed: {ex.Message}"); }
        return new IltHubState();
    }

    /// <summary>Writes the state only when its JSON differs from the last write.</summary>
    public void SaveStateIfDirty(IltHubState state)
    {
        if (_charFolder.Length == 0) return;
        try
        {
            string json = JsonSerializer.Serialize(state, RynthAiJsonContext.Default.IltHubState);
            if (json == _lastStateJson) return;
            Directory.CreateDirectory(_charFolder);
            WriteAtomic(StatePath, json);
            _lastStateJson = json;
        }
        catch (Exception ex) { _host.Log($"[IltHub] save {StatePath} failed: {ex.Message}"); }
    }

    /// <summary>Repairs null collections / out-of-range values after deserialization.</summary>
    private static void Normalize(IltHubState s)
    {
        s.ServerOptions ??= new IltServerOptionsSnapshot();
        s.ServerOptions.Bits = new Dictionary<string, int>(s.ServerOptions.Bits ?? new(), StringComparer.OrdinalIgnoreCase);
        s.Bank ??= new IltBankState();
        s.Pet ??= new IltPetState();
        s.Pet.PetLog ??= new();
        s.Pet.ShinyLog ??= new();
        s.Character ??= new IltCharacterState();
        s.Character.ItemConversions ??= new();
        s.Character.AugTargets = new Dictionary<string, int>(s.Character.AugTargets ?? new(), StringComparer.OrdinalIgnoreCase);
        s.Gear ??= new IltGearState();
        s.Gear.DispelInclusions ??= new();
        s.Gear.DispelExclusions ??= new();
        s.Games ??= new IltGamesState();
        s.SelectedTab = Math.Clamp(s.SelectedTab, 0, 4);
        s.Gear.SplitArrowTarget = Math.Clamp(s.Gear.SplitArrowTarget, 0, 10);
    }

    // ── Transactions ────────────────────────────────────────────────────────

    public List<IltBankTransaction> LoadTransactions()
    {
        try
        {
            if (_charFolder.Length > 0 && File.Exists(TxPath))
            {
                string json = File.ReadAllText(TxPath);
                var list = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.IltBankTransactionList);
                if (list != null) { _lastTxJson = json; return list; }
            }
        }
        catch (Exception ex) { _host.Log($"[IltHub] load {TxPath} failed: {ex.Message}"); }
        return new List<IltBankTransaction>();
    }

    public void SaveTransactionsIfDirty(List<IltBankTransaction> list)
    {
        if (_charFolder.Length == 0) return;
        try
        {
            if (list.Count > MaxTransactions)
                list.RemoveRange(0, list.Count - MaxTransactions);
            string json = JsonSerializer.Serialize(list, RynthAiJsonContext.Default.IltBankTransactionList);
            if (json == _lastTxJson) return;
            Directory.CreateDirectory(_charFolder);
            WriteAtomic(TxPath, json);
            _lastTxJson = json;
        }
        catch (Exception ex) { _host.Log($"[IltHub] save {TxPath} failed: {ex.Message}"); }
    }

    // ── Profiles ────────────────────────────────────────────────────────────

    /// <summary>Lists profile names. Shared ones are prefixed "shared:".</summary>
    public List<string> ListProfiles()
    {
        var names = new List<string>();
        try
        {
            if (Directory.Exists(CharProfileDir))
                names.AddRange(Directory.GetFiles(CharProfileDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>());
            if (Directory.Exists(SharedProfileDir))
                names.AddRange(Directory.GetFiles(SharedProfileDir, "*.json").Select(p => "shared:" + Path.GetFileNameWithoutExtension(p)));
        }
        catch (Exception ex) { _host.Log($"[IltHub] list profiles failed: {ex.Message}"); }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Saves the preference parts of <paramref name="state"/> as a named profile.</summary>
    public bool SaveProfile(IltHubState state, string name, bool shared, out string error)
    {
        error = string.Empty;
        string safe = SanitizeName(name);
        if (safe.Length == 0) { error = "Profile name is empty."; return false; }
        try
        {
            string dir = shared ? SharedProfileDir : CharProfileDir;
            Directory.CreateDirectory(dir);
            var copy = Clone(state);
            StripRuntime(copy);
            WriteAtomic(Path.Combine(dir, safe + ".json"), JsonSerializer.Serialize(copy, RynthAiJsonContext.Default.IltHubState));
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Loads a profile onto <paramref name="target"/>, keeping runtime data (balances,
    /// server bits, rosters, import flag) from the current state.
    /// </summary>
    public bool LoadProfile(IltHubState target, string name, out string error)
    {
        error = string.Empty;
        bool shared = name.StartsWith("shared:", StringComparison.OrdinalIgnoreCase);
        string safe = SanitizeName(shared ? name.Substring(7) : name);
        string path = Path.Combine(shared ? SharedProfileDir : CharProfileDir, safe + ".json");
        try
        {
            if (!File.Exists(path)) { error = $"Profile not found: {name}"; return false; }
            var p = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.IltHubState);
            if (p == null) { error = "Profile file is empty."; return false; }
            Normalize(p);
            target.Pet = KeepRosters(p.Pet, target.Pet);
            target.Character = p.Character;
            target.Gear = p.Gear;
            target.Games = p.Games;
            target.Bank.AutoRefreshSeconds = p.Bank.AutoRefreshSeconds;
            target.ForceLeaftideFeatures = p.ForceLeaftideFeatures;
            target.GamesHudVisible = p.GamesHudVisible;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static IltPetState KeepRosters(IltPetState incoming, IltPetState current)
    {
        incoming.PetLog = current.PetLog;
        incoming.ShinyLog = current.ShinyLog;
        return incoming;
    }

    private static IltHubState Clone(IltHubState s)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(s, RynthAiJsonContext.Default.IltHubState),
               RynthAiJsonContext.Default.IltHubState) ?? new IltHubState();

    /// <summary>Removes character-specific runtime data before a profile is written.</summary>
    private static void StripRuntime(IltHubState s)
    {
        s.ServerOptions = new IltServerOptionsSnapshot();
        var keepRefresh = s.Bank.AutoRefreshSeconds;
        s.Bank = new IltBankState { AutoRefreshSeconds = keepRefresh };
        s.Pet.PetLog = new();
        s.Pet.ShinyLog = new();
        s.UbSidecarsImported = false;
        s.WindowVisible = false;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((name ?? string.Empty).Trim().Where(c => !invalid.Contains(c)).ToArray());
    }

    // ── UtilityBelt sidecar import ──────────────────────────────────────────

    /// <summary>
    /// One-time import of UB's bankdata (balances, transactions) and item conversions.
    /// Searches Documents\Decal Plugins\UtilityBelt\&lt;world&gt;\&lt;char&gt;; when the world is
    /// unknown every world folder containing the character is tried. Returns a summary.
    /// </summary>
    public string ImportUbSidecars(IltHubState state, List<IltBankTransaction> txLog, string world, string charName)
    {
        if (string.IsNullOrWhiteSpace(charName)) return "character name unknown";
        string ubRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Decal Plugins", "UtilityBelt");
        if (!Directory.Exists(ubRoot)) return "no UtilityBelt folder";

        var charDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(world))
            charDirs.Add(Path.Combine(ubRoot, world, charName));
        try
        {
            foreach (string w in Directory.GetDirectories(ubRoot))
            {
                string d = Path.Combine(w, charName);
                if (!charDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) charDirs.Add(d);
            }
        }
        catch { /* unreadable Documents subfolder: fall through with what we have */ }

        foreach (string dir in charDirs)
        {
            string bankDir = Path.Combine(dir, "bankdata");
            if (!Directory.Exists(bankDir) && !Directory.Exists(dir)) continue;

            int parts = 0;
            parts += ImportBalances(Path.Combine(bankDir, "bankbalances.json"), state.Bank) ? 1 : 0;
            int tx = ImportTransactions(Path.Combine(bankDir, "transactions.json"), txLog);
            int conv = ImportConversions(new[]
            {
                Path.Combine(bankDir, "itemconversions.json"),
                Path.Combine(dir, "itemconversions.json"),
            }, state.Character.ItemConversions);

            if (parts + tx + conv > 0)
                return $"imported from {dir}: balances {(parts > 0 ? "yes" : "no")}, {tx} transactions, {conv} conversions";
        }
        return "no UtilityBelt bank data found for " + charName;
    }

    private bool ImportBalances(string path, IltBankState bank)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            bank.Pyreals = ReadLong(root, "Pyreals");
            bank.Luminance = ReadLong(root, "Luminance");
            bank.LegendaryKeys = ReadLong(root, "LegendaryKeys");
            bank.MythicalKeys = ReadLong(root, "MythicalKeys");
            bank.EnlightenedCoins = ReadLong(root, "EnlightenedCoins");
            bank.WeaklyEnlightenedCoins = ReadLong(root, "WeaklyEnlightenedCoins");
            if (TryGetProp(root, "LastUpdated", out var lu) && lu.ValueKind == JsonValueKind.String && lu.TryGetDateTime(out var dt))
                bank.LastUpdated = dt;
            return true;
        }
        catch (Exception ex) { _host.Log($"[IltHub] UB balances import failed: {ex.Message}"); return false; }
    }

    private int ImportTransactions(string path, List<IltBankTransaction> txLog)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
            int added = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var t = new IltBankTransaction
                {
                    Type = ReadString(e, "Type"),
                    Currency = ReadString(e, "Currency"),
                    Amount = ReadLong(e, "Amount"),
                    OtherPlayer = ReadString(e, "OtherPlayer"),
                    Description = ReadString(e, "Description"),
                };
                if (TryGetProp(e, "Timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTime(out var dt))
                    t.Timestamp = dt;
                // Skip exact duplicates so re-running the import can't double the log.
                if (txLog.Any(x => x.Timestamp == t.Timestamp && x.Amount == t.Amount && x.OtherPlayer == t.OtherPlayer)) continue;
                txLog.Add(t);
                added++;
            }
            txLog.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return added;
        }
        catch (Exception ex) { _host.Log($"[IltHub] UB transactions import failed: {ex.Message}"); return 0; }
    }

    private int ImportConversions(string[] paths, List<IltItemConversion> table)
    {
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                int added = 0;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string name = ReadString(e, "ItemName");
                    if (name.Length == 0) continue;
                    var existing = table.FirstOrDefault(c => c.ItemName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null) { existing = new IltItemConversion { ItemName = name }; table.Add(existing); added++; }
                    existing.ItemsPerConversion = Math.Max(1, (int)ReadLong(e, "ItemsPerConversion"));
                    existing.CoinsPerConversion = (int)ReadLong(e, "CoinsPerConversion");
                }
                return added;
            }
            catch (Exception ex) { _host.Log($"[IltHub] UB conversions import failed: {ex.Message}"); }
        }
        return 0;
    }

    // ── JSON helpers (case-insensitive property access) ─────────────────────

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!TryGetProp(obj, name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
        if (v.ValueKind == JsonValueKind.String) return IltParse.ParseLeadingLong(v.GetString() ?? string.Empty);
        return 0;
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (!TryGetProp(obj, name, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty,
        };
    }

    /// <summary>Write-then-rename so a crash mid-write never leaves a truncated file.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}

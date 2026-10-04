// IltEquipSuits.cs — UtilityBelt-style equipment profiles (.utl VTank loot files) (Gear tab, P2).
//
// A suit is a VTank loot profile: every carried wearable whose first matching rule is
// Keep belongs to the suit. Loading a suit:
//   1. dequip phase — equipped wearables NOT in the suit go to a pack with a free slot
//      (WorldObjectCache.FindPackFor; never into a full pack — that crashes the client);
//   2. equip phase  — suit items that aren't worn are used (AC equips on use).
// One action per 100 ms tick and only while not busy. An item is abandoned after 15
// attempts; the whole load bails after 10 s with no progress.
// Search order: RynthAi EquipProfiles\<char>, EquipProfiles, then UB's equip folders.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltEquipSuits : IIltFeature
{
    private const int MaxAttemptsPerItem = 15;
    private const long NoProgressBailMs = 10_000;
    private const long StepMs = 100;

    private readonly IltHubContext _ctx;

    // Load job (pump thread)
    private readonly Queue<int> _dequip = new();
    private readonly Queue<int> _equip = new();
    private readonly Dictionary<int, int> _attempts = new();
    private bool _running;
    private long _lastStep, _lastProgress;
    private int _doneCount;
    private volatile string _status = "idle";

    // Render snapshots
    private volatile string[] _profiles = Array.Empty<string>();
    private volatile string[] _preview = Array.Empty<string>();
    private int _selected;

    public IltEquipSuits(IltHubContext ctx) => _ctx = ctx;

    private static readonly AcObjectClass[] WearableClasses =
    {
        AcObjectClass.Armor, AcObjectClass.Clothing, AcObjectClass.Gem, AcObjectClass.Jewelry,
        AcObjectClass.MeleeWeapon, AcObjectClass.MissileWeapon, AcObjectClass.WandStaffOrb,
    };

    // ── Profile discovery ───────────────────────────────────────────────────

    private IEnumerable<string> SearchDirs()
    {
        string root = Path.Combine(IltHubStore.SharedRoot, "EquipProfiles");
        if (_ctx.CharName.Length > 0) yield return Path.Combine(root, _ctx.CharName);
        yield return root;
        string ub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Decal Plugins", "UtilityBelt");
        string world = _ctx.Options.WorldName;
        if (world.Length > 0 && _ctx.CharName.Length > 0) yield return Path.Combine(ub, world, _ctx.CharName, "equip");
        if (world.Length > 0) yield return Path.Combine(ub, world, "equip");
        yield return Path.Combine(ub, "equip");
    }

    /// <summary>All .utl profiles, first directory wins for duplicate names.</summary>
    public List<(string Name, string Path)> ListProfiles()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in SearchDirs())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "*.utl"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (!seen.ContainsKey(n)) seen[n] = f;
                }
            }
            catch (Exception ex) { RynthLog.Exception(LogCat.IltGear, ex, $"equip dir {dir}"); }
        }
        return seen.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => (k.Key, k.Value)).ToList();
    }

    private string? ResolveProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = _ctx.CharName;
        name = name!.Trim();
        if (name.EndsWith(".utl", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return ListProfiles().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Path;
    }

    /// <summary>Items the suit wants worn (first matching rule is Keep).</summary>
    private List<WorldObject>? SuitItems(string path, out string error)
    {
        error = string.Empty;
        VTankLootProfile profile;
        try { profile = VTankLootParser.Load(path); }
        catch (Exception ex) { error = "could not read profile: " + ex.Message; return null; }

        var lootCtx = new VTankLootContext(_ctx.Host, _ctx.Inventory.PlayerId) { Cache = _ctx.Inventory.Cache };
        var list = new List<WorldObject>();
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (Array.IndexOf(WearableClasses, wo.ObjectClass) < 0) continue;
            if (_ctx.Inventory.Int(wo, IltInventory.IntValidLocations) == 0) continue;
            VTankLootRule? match = null;
            foreach (var rule in profile.Rules)
                if (VTankLootEvaluator.Match(rule, wo, lootCtx)) { match = rule; break; }
            if (match != null && match.Action == VTankLootAction.Keep) list.Add(wo);
        }
        return list;
    }

    // ── Commands (pump thread) ──────────────────────────────────────────────

    public void RefreshProfileList() => _profiles = ListProfiles().Select(p => p.Name).ToArray();

    /// <summary>Lists what a load would do without doing it. Returns lines (also shown in the UI).</summary>
    public List<string> Test(string? name)
    {
        var lines = new List<string>();
        string? path = ResolveProfile(name);
        if (path == null) { lines.Add($"Profile not found: {name ?? _ctx.CharName}"); _preview = lines.ToArray(); return lines; }
        var suit = SuitItems(path, out string err);
        if (suit == null) { lines.Add(err); _preview = lines.ToArray(); return lines; }
        var suitIds = suit.Select(s => s.Id).ToHashSet();
        foreach (var wo in _ctx.Inventory.Items().Where(w => _ctx.Inventory.IsEquipped(w) && !suitIds.Contains(w.Id)
                                                             && Array.IndexOf(WearableClasses, w.ObjectClass) >= 0))
            lines.Add("dequip " + wo.Name);
        foreach (var wo in suit.Where(w => !_ctx.Inventory.IsEquipped(w))) lines.Add("equip   " + wo.Name);
        if (lines.Count == 0) lines.Add("Already wearing this suit.");
        _preview = lines.ToArray();
        return lines;
    }

    /// <summary>Starts loading a suit.</summary>
    public string Load(string? name)
    {
        RynthLog.Trace(LogCat.IltGear, $"Suits.Load(name='{name}')");
        if (_running) return "A suit load is already running.";
        if (!_ctx.Host.HasMoveItemInternal || !_ctx.Host.HasUseObject) return "Engine lacks MoveItemInternal/UseObject.";
        string? path = ResolveProfile(name);
        if (path == null) return $"Profile not found: {name ?? _ctx.CharName}";
        var suit = SuitItems(path, out string err);
        if (suit == null) return err;

        var suitIds = suit.Select(s => s.Id).ToHashSet();
        _dequip.Clear(); _equip.Clear(); _attempts.Clear();
        foreach (var wo in _ctx.Inventory.Items().Where(w => _ctx.Inventory.IsEquipped(w) && !suitIds.Contains(w.Id)
                                                             && Array.IndexOf(WearableClasses, w.ObjectClass) >= 0))
            _dequip.Enqueue(wo.Id);
        foreach (var wo in suit.Where(w => !_ctx.Inventory.IsEquipped(w))) _equip.Enqueue(wo.Id);

        if (_dequip.Count + _equip.Count == 0) return "Already wearing this suit.";
        _running = true;
        _doneCount = 0;
        _lastProgress = IltHubContext.NowMs;
        _ctx.State.Gear.LastEquipProfile = Path.GetFileNameWithoutExtension(path);
        _status = $"loading {Path.GetFileNameWithoutExtension(path)}: {_dequip.Count} off, {_equip.Count} on";
        return _status;
    }

    public void Cancel(string why)
    {
        RynthLog.Trace(LogCat.IltGear, $"Suits.Cancel(why='{why}')");
        _running = false;
        _dequip.Clear();
        _equip.Clear();
        _status = why;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (!_running || nowMs - _lastStep < StepMs) return;
        _lastStep = nowMs;
        if (nowMs - _lastProgress > NoProgressBailMs) { Cancel($"no progress for 10 s - stopped ({_doneCount} done)"); return; }
        if (_ctx.Inventory.IsBusy) return;

        var cache = _ctx.Inventory.Cache;
        if (cache == null) { Cancel("cache not ready"); return; }

        // Dequip phase first so slots are free for the suit.
        if (_dequip.Count > 0)
        {
            int id = _dequip.Peek();
            var wo = cache[id];
            if (wo == null || !_ctx.Inventory.IsEquipped(wo)) { _dequip.Dequeue(); Progress(nowMs); return; }
            if (!Attempt(id)) { _dequip.Dequeue(); return; }
            int pack = WorldObjectCache.FindPackFor(_ctx.Host, cache, includeMainPack: true, requireFree: 1);
            if (pack == 0) { Cancel("no free pack slot for dequipped items - stopped"); return; }
            _ctx.Host.MoveItemInternal(unchecked((uint)id), unchecked((uint)pack), 0, Math.Max(1, _ctx.Inventory.StackSize(wo)));
            return;
        }

        if (_equip.Count > 0)
        {
            int id = _equip.Peek();
            var wo = cache[id];
            if (wo == null || _ctx.Inventory.IsEquipped(wo)) { _equip.Dequeue(); Progress(nowMs); return; }
            if (!Attempt(id)) { _equip.Dequeue(); return; }
            _ctx.Inventory.Use(wo);
            return;
        }

        _running = false;
        _status = $"suit loaded ({_doneCount} changes)";
    }

    /// <summary>Counts an attempt; false once the item has used up its attempts.</summary>
    private bool Attempt(int id)
    {
        int n = _attempts.TryGetValue(id, out int a) ? a + 1 : 1;
        _attempts[id] = n;
        return n <= MaxAttemptsPerItem;
    }

    private void Progress(long now)
    {
        _doneCount++;
        _lastProgress = now;
    }

    public bool OnChat(string text) => false;
    public void OnLogout() => Cancel("idle");

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Equipment suits (.utl)");
        if (ImGui.SmallButton("Rescan profiles")) _ctx.Post(RefreshProfileList);
        var profiles = _profiles;
        if (profiles.Length == 0)
        {
            ImGui.TextDisabled(@"No .utl files found (C:\Games\RynthSuite\RynthAi\EquipProfiles or UtilityBelt equip folders).");
        }
        else
        {
            _selected = Math.Clamp(_selected, 0, profiles.Length - 1);
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Suit", ref _selected, profiles, profiles.Length);
            string pick = profiles[_selected];
            ImGui.SameLine();
            if (ImGui.SmallButton("Test")) _ctx.Post(() => Test(pick));
            ImGui.SameLine();
            if (ImGui.SmallButton("Load..."))
                _ctx.Confirm("Load equipment suit", $"Swap to suit '{pick}'?\nItems not in the suit are moved to your packs.",
                    () => _ctx.Chat("[ILT Hub] " + Load(pick)), "Load");
            if (_running)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) _ctx.Post(() => Cancel("cancelled by player"));
            }
        }
        ImGui.TextDisabled("Status: " + _status);
        foreach (string l in _preview) ImGui.BulletText(l);
    }
}

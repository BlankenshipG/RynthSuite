// HudController.cs — Owner of RynthAi's floating HUD windows.
//
//   * Pack item count HUD (ItemCountHud): icon + carried count for pinned items.
//   * Mini Remote (MiniRemoteHud): session stats, attack target, summon (health / time
//     left, fed by CombatHudTracker), 30 quick-use item slots, macro toggles, bank balances
//     and rebuff buttons.
//   * Inventory HUDs setup window (HudSetupUi): picks what the two HUDs show. Opened from
//     the Inventory Management settings (ImGui Advanced Settings, the Avalonia Settings
//     panel) or "/ra huds".
// Every HUD is an ordinary ImGui window, so it can be docked into / undocked from other
// ImGui windows and dragged outside the game window (multi-viewport).
//
// Threading: host reads and every game action run on the pump thread (Tick / posted
// actions). The render thread only reads the published pack snapshot and toggles simple
// flags; list edits are posted so the pump thread is the only writer.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Huds;

/// <summary>One carried item name, aggregated over every stack in the pack.</summary>
internal sealed record HudPackRow(string Name, uint Wcid, uint IconDid, long Count, int FirstId);

/// <summary>Immutable pack view published by the pump thread.</summary>
internal sealed class HudPackSnapshot
{
    public static readonly HudPackSnapshot Empty = new(Array.Empty<HudPackRow>());

    public HudPackRow[] Rows { get; }
    public Dictionary<string, HudPackRow> ByName { get; }

    public HudPackSnapshot(HudPackRow[] rows)
    {
        Rows = rows;
        ByName = new Dictionary<string, HudPackRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) ByName[r.Name] = r;
    }

    public long CountOf(string name) => ByName.TryGetValue(name, out var r) ? r.Count : 0;
}

internal sealed class HudController
{
    private const uint StypeIcon = 8;       // DataID Icon → PWD _iconID
    private const int StypeStackSize = 12;
    private const long PackScanIntervalMs = 1000;
    private const long SaveIntervalMs = 5000;

    private readonly RynthCoreHost _host;
    private readonly Func<WorldObjectCache?> _cache;
    private readonly HudStore _store;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly Dictionary<int, uint> _iconById = new();
    private readonly ItemCountHud _itemHud;
    private readonly MiniRemoteHud _miniRemote;
    private readonly HudSetupUi _setup;
    private readonly CombatHudTracker _combat;

    private volatile HudPackSnapshot _pack = HudPackSnapshot.Empty;
    private long _lastScanAt;
    private long _lastSaveAt;
    private bool _scanRequested;

    /// <summary>
    /// How long a pack item the player selected stays assignable after the game selection moves
    /// on (the combat bot re-selects monsters, so "the selected item" is often gone by the time
    /// the player clicks a slot).
    /// </summary>
    private const long RememberSelectionMs = 120_000;
    private uint _lastSeenSelection;
    private int _lastPackSelId;
    private long _lastPackSelAt;
    private volatile string _assignCandidate = string.Empty;

    public HudController(RynthCoreHost host, string charFolder, Func<WorldObjectCache?> cache,
        LegacyDashboardRenderer dashboard, Func<IltHubController?> hub, HudIconCache icons, Func<int> attackTargetId)
    {
        _host = host;
        _cache = cache;
        _store = new HudStore(charFolder);
        State = _store.Load();
        Icons = icons;
        Dashboard = dashboard;
        Hub = hub;
        _itemHud = new ItemCountHud(this);
        _miniRemote = new MiniRemoteHud(this);
        _setup = new HudSetupUi(this);
        _combat = new CombatHudTracker(host, cache, attackTargetId);
    }

    public HudState State { get; }
    public HudIconCache Icons { get; }
    public LegacyDashboardRenderer Dashboard { get; }
    public Func<IltHubController?> Hub { get; }
    public RynthCoreHost Host => _host;

    /// <summary>Latest pack view (render thread safe).</summary>
    public HudPackSnapshot Pack => _pack;

    /// <summary>Latest attack target / summon view (render thread safe).</summary>
    public CombatHudSnapshot Combat => _combat.Snapshot;

    /// <summary>
    /// Name of the pack item a slot click would assign (current or remembered selection); empty
    /// when there is none. Render thread safe.
    /// </summary>
    public string AssignCandidate => _assignCandidate;

    /// <summary>Chat translator views for the Mini Remote row; null until the translator exists.</summary>
    public RynthCore.Plugin.RynthAi.Translate.TranslateUi? Translate { get; set; }

    /// <summary>Queues work for the next pump tick (safe from the render thread).</summary>
    public void Post(Action action) => _posted.Enqueue(action);

    private static long NowMs => Environment.TickCount64;

    // ── Pump thread ─────────────────────────────────────────────────────────

    public void Tick()
    {
        int guard = 0;
        while (guard++ < 64 && _posted.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "posted HUD action"); }
        }

        long now = NowMs;
        // The pack is only scanned while something shows it.
        bool needPack = State.ShowItemHud || State.ShowMiniRemote || State.ShowSetup;
        if (needPack && (_scanRequested || now - _lastScanAt >= PackScanIntervalMs))
        {
            _scanRequested = false;
            _lastScanAt = now;
            RebuildPack();
        }

        Icons.Tick();
        if (State.ShowMiniRemote || State.ShowSetup || State.ShowItemHud) TrackPackSelection(now);

        // Target / summon polling sends health queries and appraisals, so it only runs while shown.
        if (State.ShowMiniRemote && (State.MiniShowTarget || State.MiniShowPet))
            _combat.Tick();

        if (now - _lastSaveAt >= SaveIntervalMs)
        {
            _lastSaveAt = now;
            _store.SaveIfDirty(State);
        }
    }

    public void OnLogout()
    {
        _store.SaveIfDirty(State);
        _pack = HudPackSnapshot.Empty;
        _iconById.Clear();
        _combat.OnLogout();
    }

    /// <summary>Aggregates the pack by item name and refreshes pinned entries' icon / WCID.</summary>
    private void RebuildPack()
    {
        var cache = _cache();
        if (cache == null) return;

        var byName = new Dictionary<string, HudPackRow>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var wo in cache.GetInventory())
        {
            if (wo == null || string.IsNullOrWhiteSpace(wo.Name)) continue;
            seen.Add(wo.Id);
            long count = Math.Max(1, wo.Values(StypeStackSize, 1));
            if (byName.TryGetValue(wo.Name, out var row))
            {
                byName[wo.Name] = row with { Count = row.Count + count };
                continue;
            }
            byName[wo.Name] = new HudPackRow(wo.Name, Wcid(wo), IconOf(wo), count, wo.Id);
        }

        // Forget icon lookups for items that left the pack.
        if (_iconById.Count > seen.Count + 64)
            foreach (int id in _iconById.Keys.Where(k => !seen.Contains(k)).ToList()) _iconById.Remove(id);

        var snap = new HudPackSnapshot(byName.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        _pack = snap;

        // Remember icon / WCID on pinned entries so they still draw while none are carried.
        foreach (var e in State.ItemHudItems.Concat(State.MiniRemoteSlots))
        {
            if (e.IsEmpty || !snap.ByName.TryGetValue(e.Name, out var r)) continue;
            if (r.IconDid != 0) e.IconDid = r.IconDid;
            if (r.Wcid != 0) e.Wcid = r.Wcid;
        }
    }

    private uint IconOf(WorldObject wo)
    {
        if (_iconById.TryGetValue(wo.Id, out uint icon) && icon != 0) return icon;
        if (_host.HasGetObjectDataIdProperty && _host.TryGetObjectDataIdProperty(unchecked((uint)wo.Id), StypeIcon, out icon))
            _iconById[wo.Id] = icon;
        return icon;
    }

    private uint Wcid(WorldObject wo)
        => _host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)wo.Id), out uint w) ? w : 0;

    // ── Actions (pump thread; post them from the render thread) ──────────────

    /// <summary>Rescans the pack on the next tick.</summary>
    public void RequestScan() => _scanRequested = true;

    /// <summary>Uses one carried item by name (falls back to WCID when the name changed).</summary>
    public void UseItem(HudItemEntry entry)
    {
        if (entry.IsEmpty) return;
        var cache = _cache();
        if (cache == null || !_host.HasUseObject) return;
        var inv = cache.GetInventory().Where(w => w != null).ToList();
        var wo = inv.FirstOrDefault(w => w.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                 ?? (entry.Wcid != 0 ? inv.FirstOrDefault(w => Wcid(w) == entry.Wcid) : null);
        if (wo == null) { Chat($"[RynthAi] No {entry.Name} in your pack."); return; }
        _host.UseObject(unchecked((uint)wo.Id));
        RynthLog.Trace(LogCat.Huds, $"use {wo.Name} (0x{wo.Id:X8})");
    }

    /// <summary>Carried item by object id, or null.</summary>
    private WorldObject? PackItem(int id)
        => id == 0 ? null : _cache()?.GetInventory().FirstOrDefault(w => w != null && w.Id == id);

    /// <summary>
    /// Remembers the last pack item selected in the game (native inventory, or the RynthCore
    /// Inventory window, which mirrors its clicks to the game selection). Only re-checks the
    /// pack when the selection changes.
    /// </summary>
    private void TrackPackSelection(long now)
    {
        if (!_host.HasGetSelectedItemId) return;
        uint sel = _host.GetSelectedItemId();
        if (sel != _lastSeenSelection)
        {
            _lastSeenSelection = sel;
            var wo = PackItem(unchecked((int)sel));
            if (wo != null)
            {
                _lastPackSelId = wo.Id;
                _lastPackSelAt = now;
            }
        }
        // The candidate label only feeds tooltips / menus: refresh it a few times a second.
        if (now - _lastCandidateAt < 250) return;
        _lastCandidateAt = now;
        var candidate = AssignablePackItem(quiet: true);
        _assignCandidate = candidate?.Name ?? string.Empty;
    }
    private long _lastCandidateAt;

    /// <summary>
    /// The pack item to assign: the current game selection when it is carried, otherwise the
    /// last carried item the player selected within <see cref="RememberSelectionMs"/>.
    /// </summary>
    private WorldObject? AssignablePackItem(bool quiet = false)
    {
        if (!_host.HasGetSelectedItemId)
        {
            if (!quiet) Chat("[RynthAi] This client build can't read the selected item.");
            return null;
        }
        uint selected = _host.GetSelectedItemId();
        var wo = PackItem(unchecked((int)selected));
        bool fellBack = false;
        long rememberedAge = NowMs - _lastPackSelAt;
        if (wo == null && _lastPackSelId != 0 && rememberedAge <= RememberSelectionMs)
        {
            wo = PackItem(_lastPackSelId);
            fellBack = wo != null;
        }
        // Quiet calls feed tooltips several times a second: only real assignments are traced.
        if (!quiet)
        {
            string selInfo = selected == 0 ? "none" : $"0x{selected:X8} ({(fellBack || wo == null ? DescribeCacheEntry(unchecked((int)selected)) : "in inventory")})";
            string result = wo == null ? "nothing to assign"
                : fellBack ? $"fell back to remembered 0x{_lastPackSelId:X8} '{wo.Name}' ({rememberedAge / 1000.0:0.0} s old)"
                : $"selected '{wo.Name}'";
            RynthLog.Trace(LogCat.Huds, $"assign candidate: selected={selInfo}; {result}");
        }
        if (wo == null && !quiet)
            Chat("[RynthAi] Click an item in your pack (game inventory or RynthCore Inventory) first, "
                 + "or drag it from the RynthCore Inventory onto the slot.");
        return wo;
    }

    /// <summary>The item to act on for "use the selected item" HUD actions.</summary>
    private WorldObject? SelectedPackItem() => AssignablePackItem();

    public void AddSelectedToItemHud()
    {
        var wo = SelectedPackItem();
        if (wo != null) AddToItemHud(wo.Name);
    }

    public void AddToItemHud(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || State.ItemHudItems.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var row = _pack.ByName.TryGetValue(name, out var r) ? r : null;
        State.ItemHudItems.Add(new HudItemEntry { Name = name, Wcid = row?.Wcid ?? 0, IconDid = row?.IconDid ?? 0 });
        RynthLog.Trace(LogCat.Huds, $"item HUD += '{name}' (first id 0x{(uint)(row?.FirstId ?? 0):X8}, wcid {row?.Wcid ?? 0}, in pack scan: {(row != null ? "yes" : "no")})");
    }

    /// <summary>
    /// Adds <paramref name="name"/> to the item count HUD (if missing) and shows the HUD.
    /// Icon and WCID fill in on the next pack scan. Returns the chat confirmation.
    /// </summary>
    public string AddToItemHudAndShow(string name)
    {
        name = name.Trim();
        bool already = State.ItemHudItems.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!already) AddToItemHud(name);
        State.ShowItemHud = true;
        RequestScan();
        return already ? $"{name} is already on the item count HUD." : $"Added {name} to the item count HUD.";
    }

    public void RemoveFromItemHud(string name)
        => State.ItemHudItems.RemoveAll(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves a HUD entry up (-1) or down (+1) in display order.</summary>
    public void MoveItemHudEntry(string name, int delta)
    {
        var list = State.ItemHudItems;
        int i = list.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
    }

    /// <summary>Removes HUD entries for items no longer carried.</summary>
    public void DropMissingFromItemHud()
    {
        var pack = _pack;
        int n = State.ItemHudItems.RemoveAll(e => pack.CountOf(e.Name) == 0);
        Chat(n > 0 ? $"[RynthAi] Removed {n} item(s) you no longer carry from the HUD." : "[RynthAi] Every HUD item is still in your pack.");
    }

    /// <summary>Puts the selected (or last selected) pack item into Mini Remote slot <paramref name="slot"/>.</summary>
    public void SetSlotFromSelection(int slot)
    {
        if (slot < 0 || slot >= State.MiniRemoteSlots.Count) return;
        var wo = AssignablePackItem();
        if (wo != null) AssignSlot(slot, wo);
    }

    /// <summary>
    /// Puts carried item <paramref name="objectId"/> into <paramref name="slot"/> (-1 = first empty
    /// slot). Used by drag-and-drop from the RynthCore Inventory and its "Add to Mini Remote" menu.
    /// </summary>
    public void SetSlotFromItemId(int slot, uint objectId)
    {
        var wo = PackItem(unchecked((int)objectId));
        if (wo == null)
        {
            // The cache's view of the id shows a classification miss (item filed outside inventory).
            int invCount = _cache()?.GetInventory().Count() ?? -1;
            RynthLog.Trace(LogCat.Huds, $"slot-from-id: 0x{objectId:X8} not in inventory ({invCount} items); {DescribeCacheEntry(unchecked((int)objectId))}");
            Chat("[RynthAi] That item is no longer in your pack.");
            return;
        }
        RynthLog.Trace(LogCat.Huds, $"slot-from-id: 0x{objectId:X8} = '{wo.Name}' ({wo.ObjectClass}), requested slot {(slot < 0 ? "first empty" : (slot + 1).ToString("00"))}");
        if (slot < 0) slot = State.MiniRemoteSlots.FindIndex(e => e.IsEmpty);
        if (slot < 0) { Chat("[RynthAi] Every Mini Remote slot is in use. Clear one first (right-click it)."); return; }
        if (slot >= State.MiniRemoteSlots.Count) return;
        AssignSlot(slot, wo);
    }

    private void AssignSlot(int slot, WorldObject wo)
    {
        var row = _pack.ByName.TryGetValue(wo.Name, out var r) ? r : null;
        var entry = new HudItemEntry { Name = wo.Name, Wcid = row?.Wcid ?? Wcid(wo), IconDid = row?.IconDid ?? IconOf(wo) };
        State.MiniRemoteSlots[slot] = entry;
        RynthLog.Trace(LogCat.Huds, $"assign slot {slot + 1:00} = '{wo.Name}' 0x{(uint)wo.Id:X8} (wcid {entry.Wcid}, icon 0x{entry.IconDid:X8})");
        Chat($"[RynthAi] Mini Remote slot {slot + 1:00} = {wo.Name}.");
    }

    /// <summary>How the world cache knows <paramref name="id"/> (for traces): name and class, or that it doesn't.</summary>
    private string DescribeCacheEntry(int id)
    {
        var cache = _cache();
        if (cache == null) return "no world cache";
        var wo = cache[id];
        return wo == null ? "unknown to the world cache" : $"world cache has '{wo.Name}' as {wo.ObjectClass}";
    }

    /// <summary>
    /// Remote command "remoteslot &lt;slot|first&gt; &lt;objectId&gt;" (RynthCore Inventory menu).
    /// Slot is 1-based. Returns false when the value can't be parsed.
    /// </summary>
    public bool HandleRemoteSlot(string value)
    {
        var parts = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int slot = 0;
        bool ok = parts.Length == 2 && uint.TryParse(parts[1], out _);
        if (ok)
        {
            if (parts[0].Equals("first", StringComparison.OrdinalIgnoreCase)) slot = -1;
            else if (int.TryParse(parts[0], out int n) && n >= 1) slot = n - 1;
            else ok = false;
        }
        if (!ok)
        {
            RynthLog.Trace(LogCat.Huds, $"remoteslot '{value}': can't parse (expected '<slot|first> <objectId>')");
            return false;
        }
        uint id = uint.Parse(parts[1]);
        RynthLog.Trace(LogCat.Huds, $"remoteslot '{value}': slot {(slot < 0 ? "first empty" : (slot + 1).ToString("00"))}, id 0x{id:X8}");
        SetSlotFromItemId(slot, id);
        return true;
    }

    public void ClearSlot(int slot)
    {
        if (slot >= 0 && slot < State.MiniRemoteSlots.Count) State.MiniRemoteSlots[slot] = new HudItemEntry();
    }

    public void Chat(string text)
    {
        // Every message the player sees also lands in the Huds trace.
        RynthLog.Trace(LogCat.Huds, "chat: " + text);
        if (_host.HasWriteToChat) _host.WriteToChat(text, 1);
    }

    /// <summary>
    /// "/ra huds|itemhud|remote [show|hide|toggle]" and the matching remote command.
    /// Returns the chat confirmation.
    /// </summary>
    public string HandleCommand(string which, string mode)
    {
        static bool Resolve(string m, bool current) => m.ToLowerInvariant() switch
        {
            "show" or "on" or "true" or "1" => true,
            "hide" or "off" or "false" or "0" => false,
            _ => !current,
        };

        switch (which)
        {
            case "itemhud":
                State.ShowItemHud = Resolve(mode, State.ShowItemHud);
                return State.ShowItemHud ? "Pack item count HUD shown." : "Pack item count HUD hidden.";
            case "remote":
            case "miniremote":
                State.ShowMiniRemote = Resolve(mode, State.ShowMiniRemote);
                return State.ShowMiniRemote ? "Mini Remote shown." : "Mini Remote hidden.";
            default:
                State.ShowSetup = Resolve(mode, State.ShowSetup);
                return State.ShowSetup ? "Inventory HUDs window shown." : "Inventory HUDs window hidden.";
        }
    }

    // ── Render thread ───────────────────────────────────────────────────────

    public void Render()
    {
        int pushed = LegacyDashboardRenderer.PushDashboardStyle();
        try
        {
            if (State.ShowSetup) _setup.Render();
            if (State.ShowItemHud) _itemHud.Render(); else _itemHud.OnHidden();
            if (State.ShowMiniRemote) _miniRemote.Render(); else _miniRemote.OnHidden();
        }
        finally
        {
            ImGuiNET.ImGui.PopStyleColor(pushed);
        }
    }
}

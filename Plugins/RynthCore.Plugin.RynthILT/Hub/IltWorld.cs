// IltWorld.cs - RynthILT's view of the game's objects, read straight from the host.
//
// The ILT Hub read items through RynthAi's WorldObjectCache while it lived in RynthAi. RynthILT
// has no cache of its own events, so this asks the client directly (the way RynthSort's
// InventoryReader does): the player's pack and side packs through GetContainerContents (the
// main pack's list also holds the side packs and the worn and wielded items), everything
// else the client knows through GetLiveObjectIds. Names, classes and container links are taken
// at each scan; property reads (Values) are live. A scan is kept for ScanMs, so the many
// Items() calls of one tick cost one walk; GetDirectInventory(forceRefresh: true) walks again.
// Pump thread only (the render thread only ever sees snapshots the features built).
using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One object as RynthILT sees it: identity from the last scan, properties read live.</summary>
internal sealed class WorldObject
{
    private readonly IltObjects _owner;

    internal WorldObject(IltObjects owner, int id, string name, AcObjectClass objectClass, int container)
    {
        _owner = owner;
        Id = id;
        Name = name ?? string.Empty;
        ObjectClass = objectClass;
        DirectContainerId = container;
    }

    public int Id { get; }
    public string Name { get; }
    public AcObjectClass ObjectClass { get; }

    /// <summary>The container the scan found it in (the player or a side pack); 0 off the player.</summary>
    public int DirectContainerId { get; }

    /// <summary>The container it is in now (live), else where the scan found it.</summary>
    public int Container => _owner.ContainerOf(Id, DirectContainerId);

    /// <summary>Who wields it (live; 0 = nobody).</summary>
    public int Wielder => _owner.WielderOf(Id);

    /// <summary>Where it is wielded (live; 0 = not wielded).</summary>
    public int WieldedLocation => _owner.WieldedLocationOf(Id);

    /// <summary>An int property, read live (<paramref name="defaultValue"/> when unknown).</summary>
    public int Values(int key, int defaultValue) => _owner.IntProperty(Id, unchecked((uint)key), defaultValue);
}

/// <summary>The player's items and the objects around, from host calls.</summary>
internal sealed class IltObjects
{
    // Property and type ids (AC's).
    internal const uint IntItemsCapacity = 6;
    internal const uint IntCurrentWieldedLocation = 10;
    internal const uint IntDamageType = 45;
    private const uint ItemTypeCreature = 0x00000010;
    private const uint ItemTypeContainer = 0x00000200;
    private const long ScanMs = 1000;
    private const int MainPackSlots = 102;

    private readonly RynthCoreHost _host;
    private uint[] _buf = new uint[512];
    private List<WorldObject> _inventory = new();
    private Dictionary<int, WorldObject> _byId = new();
    private long _scannedAt = long.MinValue;
    private uint _scannedPlayer;

    public IltObjects(RynthCoreHost host) => _host = host;

    /// <summary>Forgets the last scan (logout).</summary>
    public void Clear()
    {
        _inventory = new List<WorldObject>();
        _byId = new Dictionary<int, WorldObject>();
        _scannedAt = long.MinValue;
        _scannedPlayer = 0;
    }

    // ── Inventory ───────────────────────────────────────────────────────────

    /// <summary>Everything the player carries or wears (a snapshot, at most ScanMs old).</summary>
    public IEnumerable<WorldObject> GetInventory() => Scan(force: false);

    /// <summary>The same walk; <paramref name="forceRefresh"/> reads the client again now.</summary>
    public IReadOnlyList<WorldObject> GetDirectInventory(bool forceRefresh = false) => Scan(forceRefresh);

    /// <summary>A carried item by id, else any object the client knows by id (null when it knows none).</summary>
    public WorldObject? this[int id]
    {
        get
        {
            Scan(force: false);
            if (_byId.TryGetValue(id, out var wo)) return wo;
            uint uid = unchecked((uint)id);
            if (id == 0 || !_host.HasGetObjectName || !_host.TryGetObjectName(uid, out string name)) return null;
            return new WorldObject(this, id, name, Classify(uid), 0);
        }
    }

    private List<WorldObject> Scan(bool force)
    {
        long now = Environment.TickCount64;
        uint player = _host.GetPlayerId();
        if (!force && player == _scannedPlayer && now - _scannedAt < ScanMs) return _inventory;
        _scannedAt = now;
        _scannedPlayer = player;
        if (player == 0 || !_host.HasGetContainerContents) { _inventory = new(); _byId = new(); return _inventory; }

        var list = new List<WorldObject>(256);
        var byId = new Dictionary<int, WorldObject>(256);
        int pid = unchecked((int)player);
        foreach (uint id in Contents(player))
        {
            if (id == 0) continue;
            uint type = ItemType(id);
            Add(list, byId, id, type, pid);
            // Side packs: containers with room for items (foci hold none).
            if ((type & ItemTypeContainer) == 0) continue;
            if (!_host.TryGetObjectIntProperty(id, IntItemsCapacity, out int cap) || cap <= 0) continue;
            foreach (uint inner in Contents(id))
                if (inner != 0) Add(list, byId, inner, ItemType(inner), unchecked((int)id));
        }
        _inventory = list;
        _byId = byId;
        return list;
    }

    private void Add(List<WorldObject> list, Dictionary<int, WorldObject> byId, uint id, uint type, int container)
    {
        int iid = unchecked((int)id);
        if (byId.ContainsKey(iid)) return;
        string name = _host.HasGetObjectName && _host.TryGetObjectName(id, out string n) ? n ?? string.Empty : string.Empty;
        var wo = new WorldObject(this, iid, name, ClassifyByItemType(type), container);
        list.Add(wo);
        byId[iid] = wo;
    }

    private uint[] Contents(uint container)
    {
        int n = _host.GetContainerContents(container, _buf);
        if (n >= _buf.Length)
        {
            _buf = new uint[Math.Max(n + 64, _buf.Length * 2)];
            n = _host.GetContainerContents(container, _buf);
        }
        if (n <= 0) return Array.Empty<uint>();
        var ids = new uint[Math.Min(n, _buf.Length)];
        Array.Copy(_buf, ids, ids.Length);
        return ids;
    }

    // ── The world around ────────────────────────────────────────────────────

    /// <summary>Creatures the client knows (monsters, NPCs, vendors, pets), not carried.</summary>
    public IEnumerable<WorldObject> GetLandscape() => Around(creaturesOnly: true);

    /// <summary>Every object the client knows that the player doesn't carry.</summary>
    public IEnumerable<WorldObject> GetLandscapeObjects() => Around(creaturesOnly: false);

    private List<WorldObject> Around(bool creaturesOnly)
    {
        var result = new List<WorldObject>();
        if (!_host.HasGetLiveObjectIds) return result;
        Scan(force: false);
        uint player = _host.GetPlayerId();
        foreach (uint id in _host.GetLiveObjectIds())
        {
            if (id == 0 || id == player || _byId.ContainsKey(unchecked((int)id))) continue;
            uint type = ItemType(id);
            if (creaturesOnly && (type & ItemTypeCreature) == 0) continue;
            if (!_host.HasGetObjectName || !_host.TryGetObjectName(id, out string name) || string.IsNullOrEmpty(name)) continue;
            result.Add(new WorldObject(this, unchecked((int)id), name, ClassifyByItemType(type), 0));
        }
        return result;
    }

    /// <summary>Metres between two objects (double.MaxValue when either position can't be read).</summary>
    public double Distance(int id1, int id2)
    {
        if (!_host.TryGetObjectPosition(unchecked((uint)id1), out uint cell1, out float x1, out float y1, out float z1)) return double.MaxValue;
        if (!_host.TryGetObjectPosition(unchecked((uint)id2), out uint cell2, out float x2, out float y2, out float z2)) return double.MaxValue;
        float gx1 = ((cell1 >> 24) & 0xFF) * 192f + x1;
        float gy1 = ((cell1 >> 16) & 0xFF) * 192f + y1;
        float gx2 = ((cell2 >> 24) & 0xFF) * 192f + x2;
        float gy2 = ((cell2 >> 16) & 0xFF) * 192f + y2;
        float dx = gx1 - gx2, dy = gy1 - gy2, dz = z1 - z2;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return double.IsFinite(d) ? d : double.MaxValue;
    }

    // ── Live reads ──────────────────────────────────────────────────────────

    internal int IntProperty(int id, uint stype, int defaultValue)
        => _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(unchecked((uint)id), stype, out int v) ? v : defaultValue;

    internal int ContainerOf(int id, int scanned)
    {
        if (_host.HasGetObjectOwnershipInfo && _host.TryGetObjectOwnershipInfo(unchecked((uint)id), out uint container, out _, out _))
            return unchecked((int)container);
        return scanned;
    }

    internal int WielderOf(int id)
    {
        if (_host.HasGetObjectOwnershipInfo && _host.TryGetObjectOwnershipInfo(unchecked((uint)id), out _, out uint wielder, out _))
            return unchecked((int)wielder);
        return _host.HasGetObjectWielderInfo && _host.TryGetObjectWielderInfo(unchecked((uint)id), out uint w, out _) ? unchecked((int)w) : 0;
    }

    internal int WieldedLocationOf(int id)
    {
        if (_host.HasGetObjectOwnershipInfo && _host.TryGetObjectOwnershipInfo(unchecked((uint)id), out _, out uint wielder, out uint location) && wielder != 0)
            return unchecked((int)location);
        return Math.Max(0, IntProperty(id, IntCurrentWieldedLocation, 0));
    }

    private uint ItemType(uint id) => _host.HasGetItemType && _host.TryGetItemType(id, out uint t) ? t : 0;

    private AcObjectClass Classify(uint id) => ClassifyByItemType(ItemType(id));

    // ── Packs ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A side pack with at least <paramref name="requireFree"/> free slots (the one with the most),
    /// else the main pack when <paramref name="includeMainPack"/> and it has a slot, else 0. Foci
    /// and packs whose capacity isn't known never qualify (a move into a full pack crashes the
    /// client). RynthAi's WorldObjectCache.FindPackFor, on this view.
    /// </summary>
    public static int FindPackFor(RynthCoreHost host, IltObjects? objects, bool includeMainPack, int requireFree)
    {
        if (objects == null) return 0;
        int playerId = unchecked((int)host.GetPlayerId());
        if (playerId == 0) return 0;
        if (requireFree < 1) requireFree = 1;
        var inv = objects.GetDirectInventory(forceRefresh: true);
        int bestPack = 0, bestFree = 0, mainUsed = 0;
        foreach (var p in inv)
        {
            if (p.DirectContainerId != playerId) continue;
            bool isPack = p.ObjectClass == AcObjectClass.Container;
            bool isFoci = p.ObjectClass == AcObjectClass.Foci || p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isPack && !isFoci && p.WieldedLocation <= 0 && p.Wielder == 0) mainUsed++;
            if (!isPack || isFoci) continue;
            int capacity = p.Values(unchecked((int)IntItemsCapacity), 0);
            if (capacity <= 0) continue;
            int used = 0;
            foreach (var it in inv)
                if (it.DirectContainerId == p.Id && it.ObjectClass != AcObjectClass.Container) used++;
            int free = capacity - used;
            if (free >= requireFree && free > bestFree) { bestFree = free; bestPack = p.Id; }
        }
        if (bestPack != 0) return bestPack;
        if (!includeMainPack) return 0;
        return MainPackSlots - mainUsed > 0 ? playerId : 0;
    }

    // ── Classification (RynthAi's WorldObjectCache.ClassifyByItemType) ──────

    internal static AcObjectClass ClassifyByItemType(uint t)
    {
        if (t == 0) return AcObjectClass.Unknown;
        if ((t & 0x00000010) != 0) return AcObjectClass.Monster;
        if ((t & 0x00040000) != 0) return AcObjectClass.TradeNote;
        if ((t & 0x00080000) != 0) return AcObjectClass.ManaStone;
        if ((t & 0x00001000) != 0) return AcObjectClass.SpellComponent;
        if ((t & 0x00000800) != 0) return AcObjectClass.Gem;
        if ((t & 0x00004000) != 0) return AcObjectClass.Key;
        if ((t & 0x10000000) != 0) return AcObjectClass.Lifestone;
        if ((t & 0x40000000) != 0) return AcObjectClass.Salvage;
        if ((t & 0x20000000) != 0) return AcObjectClass.Ust;
        if ((t & 0x00400000) != 0) return AcObjectClass.BaseCooking;
        if ((t & 0x00800000) != 0) return AcObjectClass.BaseAlchemy;
        if ((t & 0x02000000) != 0) return AcObjectClass.BaseFletching;
        if ((t & 0x04000000) != 0) return AcObjectClass.CraftedAlchemy;
        if ((t & 0x08000000) != 0) return AcObjectClass.CraftedFletching;
        if ((t & 0x00100000) != 0) return AcObjectClass.Services;
        if ((t & 0x00010000) != 0) return AcObjectClass.Portal;
        if ((t & 0x00002000) != 0) return AcObjectClass.Book;
        if ((t & 0x00000200) != 0) return AcObjectClass.Container;
        if ((t & 0x00008000) != 0) return AcObjectClass.WandStaffOrb;
        if ((t & 0x00000100) != 0) return AcObjectClass.MissileWeapon;
        if ((t & 0x00000002) != 0) return AcObjectClass.Armor;
        if ((t & 0x00000001) != 0) return AcObjectClass.MeleeWeapon;
        if ((t & 0x00000004) != 0) return AcObjectClass.Clothing;
        if ((t & 0x00000008) != 0) return AcObjectClass.Jewelry;
        if ((t & 0x00000020) != 0) return AcObjectClass.Food;
        if ((t & 0x00000040) != 0) return AcObjectClass.Money;
        if ((t & 0x00000080) != 0) return AcObjectClass.Misc;
        return AcObjectClass.Unknown;
    }
}

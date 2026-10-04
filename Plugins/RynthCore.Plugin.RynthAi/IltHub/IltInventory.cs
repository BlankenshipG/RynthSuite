// IltInventory.cs — Shared item / player-property helper for every ILT Hub feature.
//
// One place for: WCID lookups (cached — the engine call is cheap but not free), stack
// counting, "is it equipped", appraisal requests (throttled), charm ON/OFF status from the
// appraisal Use text, and player int/bool/quad reads. Features never talk to the cache or
// the host for item facts directly, so behaviour (and its edge cases) stays consistent.
// Pump-thread only.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>Registered ACECustom charm: display name and every known WCID (base + tier variants).</summary>
internal sealed record IltCharmDef(string Name, uint[] Wcids);

internal sealed class IltInventory
{
    // ── Well-known WCIDs (ACECustom content) ────────────────────────────────
    public const uint WcidPyreal = 273;
    public const uint WcidEnlightenedCoin = 300004;
    public const uint WcidEnlToken = 300000;
    public const uint WcidEnlMedallion = 90000217;
    public const uint WcidEnlSigil = 300101189;
    public const uint WcidEnlCrest = 98769999;
    public const uint WcidEncapsulatedSpirit = 49485;
    public const uint WcidRuneOfDispel = 30133;
    public const uint WcidSummonRefillCharm = 78780030;
    public const uint WcidUniversalMasteryCharm = 78780031;
    public static readonly uint[] WcidInfiniteCastingStone = { 777700019, 777700055 };
    public const uint WcidRedAetheriaChunk = 42636;
    public const uint WcidBlueAetheriaChunk = 42635;

    // ── Property ids used by several features ───────────────────────────────
    public const uint IntStackSize = 12;
    public const uint IntValidLocations = 9;
    public const uint IntLevel = 25;
    public const uint IntMaxStructure = 91;
    public const uint IntStructure = 92;
    public const uint IntNumTimesTinkered = 171;
    public const uint IntImbuedEffect = 179;
    public const uint IntCleaving = 292;
    public const uint IntItemMaxLevel = 319;
    public const uint IntSummoningMastery = 362;
    public const uint IntEnlightenment = 390;
    public const uint IntSplitArrowCount = 9031;
    public const uint IntMaterialType = 131;
    public const uint IntUses = 92;
    public const uint StrUse = 14;
    public const uint QuadTotalXp = 1;
    public const uint QuadAvailableXp = 2;
    public const uint QuadItemTotalXp = 4;
    public const uint BoolNeverChunk = 9030;
    public const uint BoolPyrealRefillActive = 9049;
    public const uint BoolUniversalMasteryActive = 50038;

    /// <summary>Server-registered charms (ACECustom). Guardian Hand has no published WCID — see IltGear.</summary>
    public static readonly IltCharmDef[] Charms =
    {
        new("Mana Barrier",     new uint[] { 777700001, 777700054, 777710004, 777720004 }),
        new("Infinite Casting", WcidInfiniteCastingStone),
        new("Asheron's Favor",  new uint[] { 777700020, 777710002, 777720002 }),
        new("Artisan's",        new uint[] { 777700021, 777710003, 777720003 }),
        new("Shrapnel",         new uint[] { 777700022 }),
        new("Agony",            new uint[] { 777700023 }),
        new("Split Cast",       new uint[] { 777700024 }),
        new("Explosive Arrow",  new uint[] { 777700025, 777710005, 777720005 }),
        new("Omni Strike",      new uint[] { 777700026 }),
        new("Fork",             new uint[] { 777700027, 777710007, 777720007 }),
        new("Auto-Rebuff",      new uint[] { 777700300 }),
        new("Summon Essence Refill",       new uint[] { WcidSummonRefillCharm }),
        new("Universal Summoning Mastery", new uint[] { WcidUniversalMasteryCharm }),
    };

    private readonly RynthCoreHost _host;
    private readonly Func<WorldObjectCache?> _cache;
    private readonly Dictionary<int, uint> _wcidCache = new();
    private readonly Dictionary<int, long> _lastIdRequest = new();

    /// <summary>Don't re-request appraisal for the same object more often than this.</summary>
    private const long IdRequestThrottleMs = 30_000;

    public IltInventory(RynthCoreHost host, Func<WorldObjectCache?> cache)
    {
        _host = host;
        _cache = cache;
    }

    public RynthCoreHost Host => _host;
    public WorldObjectCache? Cache => _cache();
    public uint PlayerId => _host.GetPlayerId();

    // ── Player state ────────────────────────────────────────────────────────

    /// <summary>The client busy hourglass is up (an action is in progress).</summary>
    public bool IsBusy => _host.HasGetBusyState && _host.GetBusyState() != 0;

    /// <summary>Player is in peace mode (combat mode NonCombat). True when the engine can't tell.</summary>
    public bool InPeaceMode => !_host.HasGetCurrentCombatMode || _host.GetCurrentCombatMode() == CombatMode.NonCombat;

    public int PlayerInt(uint stype, int fallback = 0)
        => _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(PlayerId, stype, out int v) ? v : fallback;

    public bool PlayerBool(uint stype)
        => _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(PlayerId, stype, out bool v) && v;

    public long PlayerQuad(uint stype)
        => _host.HasGetObjectQuadProperty && _host.TryGetObjectQuadProperty(PlayerId, stype, out long v) ? v : 0;

    /// <summary>Current/max health from the engine vitals read (0/0 when unavailable).</summary>
    public (uint Cur, uint Max) PlayerHealth()
    {
        if (_host.HasGetPlayerVitals && _host.TryGetPlayerVitals(out uint h, out uint mh, out _, out _, out _, out _))
            return (h, mh);
        return (0, 0);
    }

    // ── Item facts ──────────────────────────────────────────────────────────

    /// <summary>All items the player carries or wears (snapshot).</summary>
    public IEnumerable<WorldObject> Items() => _cache()?.GetInventory() ?? Enumerable.Empty<WorldObject>();

    /// <summary>Weenie class id, cached per object (0 when unknown).</summary>
    public uint Wcid(WorldObject wo)
    {
        if (_wcidCache.TryGetValue(wo.Id, out uint w) && w != 0) return w;
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)wo.Id), out w))
            _wcidCache[wo.Id] = w;
        return w;
    }

    public int Int(WorldObject wo, uint stype, int fallback = 0) => wo.Values(unchecked((int)stype), fallback);

    public bool Bool(WorldObject wo, uint stype)
        => _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(unchecked((uint)wo.Id), stype, out bool v) && v;

    public long Quad(WorldObject wo, uint stype)
        => _host.HasGetObjectQuadProperty && _host.TryGetObjectQuadProperty(unchecked((uint)wo.Id), stype, out long v) ? v : 0;

    public string Str(WorldObject wo, uint stype)
        => _host.HasGetObjectStringProperty && _host.TryGetObjectStringProperty(unchecked((uint)wo.Id), stype, out string v) ? v ?? string.Empty : string.Empty;

    /// <summary>Stack size (1 for non-stackables).</summary>
    public int StackSize(WorldObject wo) => Math.Max(1, Int(wo, IntStackSize, 1));

    /// <summary>Remaining uses on a structure item (essences, kits, salvage bags).</summary>
    public int Uses(WorldObject wo) => Int(wo, IntStructure, 0);

    public bool IsEquipped(WorldObject wo)
        => wo.WieldedLocation > 0 || (wo.Wielder != 0 && unchecked((uint)wo.Wielder) == PlayerId);

    public bool HasAppraisal(WorldObject wo)
        => !_host.HasHasAppraisalData || _host.HasAppraisalData(unchecked((uint)wo.Id));

    /// <summary>Requests appraisal data, throttled per object. Returns true if a request was sent.</summary>
    public bool RequestAppraisal(WorldObject wo)
    {
        if (!_host.HasRequestId) return false;
        long now = Environment.TickCount64;
        if (_lastIdRequest.TryGetValue(wo.Id, out long last) && now - last < IdRequestThrottleMs) return false;
        _lastIdRequest[wo.Id] = now;
        return _host.RequestId(unchecked((uint)wo.Id));
    }

    // ── Finders / counters ──────────────────────────────────────────────────

    public WorldObject? FindByWcid(params uint[] wcids)
        => Items().FirstOrDefault(wo => Array.IndexOf(wcids, Wcid(wo)) >= 0);

    public IEnumerable<WorldObject> AllByWcid(params uint[] wcids)
        => Items().Where(wo => Array.IndexOf(wcids, Wcid(wo)) >= 0);

    public WorldObject? FindByName(Func<string, bool> predicate)
        => Items().FirstOrDefault(wo => predicate(wo.Name));

    /// <summary>Total stack count of items with any of the given WCIDs.</summary>
    public long CountByWcid(params uint[] wcids)
        => AllByWcid(wcids).Sum(wo => (long)StackSize(wo));

    /// <summary>Total stack count of items whose name matches exactly (case-insensitive).</summary>
    public long CountByName(string name)
        => Items().Where(wo => wo.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(wo => (long)StackSize(wo));

    /// <summary>True when the player carries any spell components or an Infinite Casting Stone.</summary>
    public bool HasCastingSupplies()
        => FindByWcid(WcidInfiniteCastingStone) != null
           || Items().Any(wo => wo.ObjectClass == AcObjectClass.SpellComponent);

    /// <summary>Free main-pack slots (best effort: capacity minus direct children; -1 when unknown).</summary>
    public int FreeMainPackSlots()
    {
        var cache = _cache();
        if (cache == null) return -1;
        int player = unchecked((int)PlayerId);
        int capacity = PlayerInt((uint)LongValueKey.ItemsCapacity, 102);
        int used = cache.GetDirectInventory()
            .Count(wo => wo.Container == player && wo.WieldedLocation <= 0 && wo.ObjectClass != AcObjectClass.Container);
        return Math.Max(0, capacity - used);
    }

    // ── Charms ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ON/OFF/Unknown from the appraisal Use text ("... Status: ON"). Requests appraisal
    /// (throttled) when the text isn't loaded yet.
    /// </summary>
    public IltTri CharmStatus(WorldObject charm)
    {
        string use = Str(charm, StrUse);
        if (use.Length == 0)
        {
            RequestAppraisal(charm);
            return IltTri.Unknown;
        }
        if (use.Contains("Status: ON", StringComparison.OrdinalIgnoreCase)) return IltTri.On;
        if (use.Contains("Status: OFF", StringComparison.OrdinalIgnoreCase)) return IltTri.Off;
        return IltTri.Unknown;
    }

    /// <summary>Charm definition for an item (by WCID), or null.</summary>
    public IltCharmDef? CharmDefFor(WorldObject wo)
    {
        uint w = Wcid(wo);
        if (w == 0) return null;
        return Charms.FirstOrDefault(c => Array.IndexOf(c.Wcids, w) >= 0);
    }

    /// <summary>
    /// Every charm the player carries: registered WCIDs plus anything whose Use text starts
    /// with "Charm [Tier" (generic ACECustom charm format, catches charms added later).
    /// </summary>
    public List<WorldObject> CarriedCharms()
    {
        var list = new List<WorldObject>();
        foreach (var wo in Items())
        {
            if (CharmDefFor(wo) != null) { list.Add(wo); continue; }
            if (wo.ObjectClass is AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon or AcObjectClass.Armor
                or AcObjectClass.Clothing or AcObjectClass.Container or AcObjectClass.Corpse)
                continue;
            if (Str(wo, StrUse).StartsWith("Charm [Tier", StringComparison.OrdinalIgnoreCase)
                || wo.Name.EndsWith(" Charm", StringComparison.OrdinalIgnoreCase))
                list.Add(wo);
        }
        return list;
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    public bool Use(WorldObject wo) => _host.HasUseObject && _host.UseObject(unchecked((uint)wo.Id));

    public bool UseOn(WorldObject source, uint targetId)
        => _host.HasUseObjectOn && _host.UseObjectOn(unchecked((uint)source.Id), targetId);

    /// <summary>Forgets cached WCIDs / appraisal throttles (logout).</summary>
    public void Reset()
    {
        _wcidCache.Clear();
        _lastIdRequest.Clear();
    }
}

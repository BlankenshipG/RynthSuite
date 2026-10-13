// MiniRemoteInventory.cs — Item / player-property helper for the Mini Remote's universal tabs (Pets,
// the VTank .usd importer). The universal part of the ILT Hub's IltInventory (SilentKelpie's; the
// ILT Hub went to the RynthILT plugin 2026-10-13, which has its own copy for its ILT features).
//
// One place for: WCID lookups (cached), stack / use counts, "is it equipped", appraisal requests
// (throttled), the Summon Essence Refill / Universal Summoning Mastery charm status (ACECustom
// items the Pets tab's refill path reads; harmless anywhere else) and player int/bool reads.
// Pump-thread only.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>Charm ON / OFF read from its appraisal text.</summary>
internal enum CharmState { Unknown = -1, Off = 0, On = 1 }

internal sealed class MiniRemoteInventory
{
    // ── Well-known WCIDs (ACECustom content the Pets tab recognises) ─────────
    public const uint WcidEncapsulatedSpirit = 49485;
    public const uint WcidSummonRefillCharm = 78780030;
    public const uint WcidUniversalMasteryCharm = 78780031;
    public static readonly uint[] WcidInfiniteCastingStone = { 777700019, 777700055 };

    // ── Property ids ────────────────────────────────────────────────────────
    public const uint IntStackSize = 12;
    public const uint IntStructure = 92;
    // Crafting tab (tinker runs): how often an item was tinkered, its imbue, a salvage bag's material.
    public const uint IntNumTimesTinkered = 171;
    public const uint IntImbuedEffect = 179;
    public const uint IntMaterialType = 131;
    public const uint StrUse = 14;
    public const uint BoolPyrealRefillActive = 9049;
    public const uint BoolUniversalMasteryActive = 50038;

    private readonly RynthCoreHost _host;
    private readonly Func<WorldObjectCache?> _cache;
    private readonly Dictionary<int, uint> _wcidCache = new();
    private readonly Dictionary<int, long> _lastIdRequest = new();

    /// <summary>Don't re-request appraisal for the same object more often than this.</summary>
    private const long IdRequestThrottleMs = 30_000;

    public MiniRemoteInventory(RynthCoreHost host, Func<WorldObjectCache?> cache)
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

    public bool PlayerBool(uint stype)
        => _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(PlayerId, stype, out bool v) && v;

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

    public string Str(WorldObject wo, uint stype)
        => _host.HasGetObjectStringProperty && _host.TryGetObjectStringProperty(unchecked((uint)wo.Id), stype, out string v) ? v ?? string.Empty : string.Empty;

    /// <summary>Remaining uses on a structure item (essences, kits, salvage bags).</summary>
    public int Uses(WorldObject wo) => Int(wo, IntStructure, 0);

    /// <summary>A bool property of an object (false when unknown or the engine can't read it).</summary>
    public bool Bool(WorldObject wo, uint stype)
        => _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(unchecked((uint)wo.Id), stype, out bool v) && v;

    /// <summary>Stack size (1 for a non-stack).</summary>
    public int StackSize(WorldObject wo) => Math.Max(1, Int(wo, IntStackSize, 1));

    /// <summary>Worn or wielded by the player.</summary>
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

    public WorldObject? FindByWcid(params uint[] wcids)
        => Items().FirstOrDefault(wo => Array.IndexOf(wcids, Wcid(wo)) >= 0);

    public IEnumerable<WorldObject> AllByWcid(params uint[] wcids)
        => Items().Where(wo => Array.IndexOf(wcids, Wcid(wo)) >= 0);

    /// <summary>
    /// True when the player carries spell components or an Infinite Casting Stone that isn't
    /// known to be switched OFF (an OFF stone doesn't waive components).
    /// </summary>
    public bool HasCastingSupplies()
        => AllByWcid(WcidInfiniteCastingStone).Any(stone => CharmStatus(stone) != CharmState.Off)
           || Items().Any(wo => wo.ObjectClass == AcObjectClass.SpellComponent);

    /// <summary>
    /// ON/OFF/Unknown from the appraisal Use text ("... Status: ON"). Requests appraisal
    /// (throttled) when the text isn't loaded yet.
    /// </summary>
    public CharmState CharmStatus(WorldObject charm)
    {
        string use = Str(charm, StrUse);
        if (use.Length == 0)
        {
            RequestAppraisal(charm);
            return CharmState.Unknown;
        }
        if (use.Contains("Status: ON", StringComparison.OrdinalIgnoreCase)) return CharmState.On;
        if (use.Contains("Status: OFF", StringComparison.OrdinalIgnoreCase)) return CharmState.Off;
        return CharmState.Unknown;
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    public bool Use(WorldObject wo) => _host.HasUseObject && _host.UseObject(unchecked((uint)wo.Id));

    /// <summary>Forgets cached WCIDs / appraisal throttles (logout).</summary>
    public void Reset()
    {
        _wcidCache.Clear();
        _lastIdRequest.Clear();
    }
}

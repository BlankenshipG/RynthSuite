using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthSort;

/// <summary>
/// The controller's view of the game through the engine host. Plugin pump thread only (the
/// plugin's tick), which is also where RynthAi's interfaces may be called.
/// </summary>
internal sealed unsafe class HostSortWorld : ISortWorld
{
    private readonly RynthCoreHost _host;
    private readonly InventoryReader _reader;
    private readonly LootWatch _loot;
    private readonly Func<bool> _vendorOpenByEvent;
    private uint[] _groundBuf = new uint[256];

    public HostSortWorld(RynthCoreHost host, InventoryReader reader, LootWatch loot, Func<bool> vendorOpenByEvent)
    {
        _host = host;
        _reader = reader;
        _loot = loot;
        _vendorOpenByEvent = vendorOpenByEvent;
    }

    public SortInventory? ReadInventory() => _reader.Read(_host);

    public GateInputs ReadGate(bool sortInCombat, long nowMs)
    {
        var g = new GateInputs { SortInCombat = sortInCombat };
        g.InPortalSpace = _host.HasIsPortaling && _host.IsPortaling();

        uint ground = _host.HasGetGroundContainerId ? _host.GetGroundContainerId() : 0;
        int count = 0;
        if (ground != 0)
        {
            count = _host.HasGetNumContainedItems ? _host.GetNumContainedItems(ground) : -1;
            if (count < 0) count = _host.GetContainerContents(ground, _groundBuf);
        }
        g.Looting = _loot.Update(ground, count, nowMs);

        g.VendorOpen = _host.HasVendorTrade ? _host.TryGetVendorInfo(out _) : _vendorOpenByEvent();
        g.TradeOpen = _host.HasTrade && _host.TryGetTradeState(out TradeState trade) && trade.IsOpen;
        g.ClientBusy = _host.HasGetBusyState && _host.GetBusyState() != 0;
        g.Animating = _host.HasGetCastBusyState && _host.GetCastBusyState() != 0;
        g.Bot = ReadRynthAi(_host);
        return g;
    }

    public bool SendMove(in SortMove move)
    {
        uint id = move.Item.Id;
        if (move.MergeInto != 0 && _host.HasMergeStackInternal)
            return _host.MergeStackInternal(id, move.MergeInto);
        return _host.HasMoveItemInternal && _host.MoveItemInternal(id, move.Dest.Id, 0, Math.Max(1, move.Item.Stack));
    }

    public MoveObservation Observe(uint itemId)
    {
        var o = new MoveObservation();
        if (_host.HasGetObjectOwnershipInfo && _host.TryGetObjectOwnershipInfo(itemId, out uint container, out uint wielder, out _))
        {
            o.Exists = true;
            o.Container = container;
            o.Wielder = wielder;
        }
        else
        {
            // No ownership info: the object is gone (merged into a stack) when it has no name either.
            o.Exists = _host.TryGetObjectName(itemId, out string n) && n.Length > 0;
            o.Container = 0xFFFFFFFF;
        }
        o.Stack = _host.TryGetObjectIntProperty(itemId, 12, out int s) && s > 0 ? s : 1;
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out uint error, out uint type, out uint obj))
        {
            o.ErrorSeq = seq;
            o.Error = error;
            o.ErrorEventType = type;
            o.ErrorObject = obj;
        }
        return o;
    }

    public int ErrorSeqNow() =>
        _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;

    // ── RynthAi ─────────────────────────────────────────────────────────────

    /// <summary>
    /// RynthAi's activity bits: its "RynthAi.Activity" interface; on an older RynthAi without it,
    /// the bot state from "RynthAi.Status" (macro and action); null when RynthAi isn't loaded or not
    /// ready. Resolved again every call: a RynthAi reload frees the tables.
    /// </summary>
    internal static RynthAiActivity? ReadRynthAi(RynthCoreHost host)
    {
        if (!host.HasGetPluginInterface) return null;
        try
        {
            var act = (RynthAiActivityApiV1*)host.GetPluginInterface("RynthAi", RynthAiActivityApiV1.InterfaceName, RynthAiActivityApiV1.InterfaceVersion);
            if (act != null && act->Version >= 1 && act->StructSize >= (uint)sizeof(RynthAiActivityApiV1) && act->GetActivityFlags != null)
            {
                uint flags = 0;
                return act->GetActivityFlags(&flags) != 0 ? (RynthAiActivity)flags : null;
            }
            var status = (RynthAiStatusApiV1*)host.GetPluginInterface("RynthAi", RynthAiStatusApiV1.InterfaceName, RynthAiStatusApiV1.InterfaceVersion);
            if (status != null && status->Version >= 1 && status->StructSize >= (uint)sizeof(RynthAiStatusApiV1) && status->GetStatusJson != null)
            {
                byte* r = null;
                if (status->GetStatusJson(&r) != 0 && r != null)
                    return FromStatusJson(Marshal.PtrToStringUTF8((IntPtr)r));
            }
        }
        catch
        {
            // A RynthAi mid-reload: treat as not there this tick.
        }
        return null;
    }

    /// <summary>The bits an older RynthAi's status JSON ({macro, action, ...}) gives (no server-wait bit).</summary>
    internal static RynthAiActivity? FromStatusJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '{') return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            RynthAiActivity f = RynthAiActivity.None;
            if (root.TryGetProperty("macro", out JsonElement m) && m.ValueKind == JsonValueKind.True) f |= RynthAiActivity.MacroRunning;
            string action = root.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            if ((f & RynthAiActivity.MacroRunning) != 0)
            {
                // The display label: Buffing shows as Healing / Restoring mana / Restoring stamina too.
                f |= action switch
                {
                    "" or "Default" => RynthAiActivity.None,
                    "Combat" => RynthAiActivity.Combat,
                    "Looting" => RynthAiActivity.Looting,
                    "Salvaging" => RynthAiActivity.Salvaging,
                    "Navigating" or "Following" => RynthAiActivity.Navigating,
                    _ => RynthAiActivity.Buffing,
                };
            }
            return f;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

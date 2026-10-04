using System;
using RynthCore.Plugin.RynthAi.ItemInfo;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra iteminfo — Mag-style one-line item info (ratings, damage, spells, slayer, imbues …),
/// ported from UtilityBelt ub-IT / Mag-Tools ItemInfo. Partial class of RynthAiPlugin.
///
///   /ra iteminfo              describe the selected item (IDs it first when needed)
///   /ra iteminfo on|off       describe every item you select
///   /ra iteminfo value on|off append value + burden
///   /ra iteminfo verbose on|off  list every spell instead of Mag's filter
/// </summary>
public sealed partial class RynthAiPlugin
{
    // One pending describe at a time: the newest request replaces the previous one.
    private int  _itemInfoPendingId;
    private long _itemInfoPendingSince;
    // Appraisal normally lands in well under a second; past this, print what the client has.
    private const long ItemInfoIdTimeoutMs = 3000;

    private void HandleItemInfoCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : string.Empty;

        if (settings != null && sub is "value" or "verbose")
        {
            bool? on = ParseOnOff(parts.Length >= 4 ? parts[3] : null);
            if (sub == "value")
            {
                if (on.HasValue) settings.ItemInfoShowValueBurden = on.Value;
                ChatLine($"[RynthAi] Item info value/burden: {(settings.ItemInfoShowValueBurden ? "ON" : "OFF")}");
            }
            else
            {
                if (on.HasValue) settings.ItemInfoVerboseSpells = on.Value;
                ChatLine($"[RynthAi] Item info all spells: {(settings.ItemInfoVerboseSpells ? "ON" : "OFF")}");
            }
            return;
        }

        bool? toggle = ParseOnOff(sub.Length > 0 ? sub : null);
        if (toggle.HasValue)
        {
            if (settings == null) { ChatLine("[RynthAi] Settings not ready (log in first)."); return; }
            settings.ItemInfoOnSelect = toggle.Value;
            ChatLine($"[RynthAi] Item info on select: {(toggle.Value ? "ON" : "OFF")}");
            return;
        }

        if (!Host.HasGetSelectedItemId) { ChatLine("[RynthAi] Host does not expose the selected item."); return; }
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) { ChatLine("[RynthAi] No item selected. Click an item first."); return; }
        QueueItemInfo(unchecked((int)selected), requestId: true);
    }

    /// <summary>
    /// Describe <paramref name="itemId"/> now if its appraisal is cached, otherwise (optionally)
    /// request an ID and print from <see cref="TickItemInfo"/> once it lands or times out.
    /// </summary>
    internal void QueueItemInfo(int itemId, bool requestId)
    {
        if (itemId == 0) return;
        uint uid = unchecked((uint)itemId);
        if (!Host.HasHasAppraisalData || Host.HasAppraisalData(uid))
        {
            _itemInfoPendingId = 0;
            PrintItemInfo(itemId, identified: true);
            return;
        }
        if (requestId && Host.HasRequestId) Host.RequestId(uid);
        _itemInfoPendingId = itemId;
        _itemInfoPendingSince = Environment.TickCount64;
    }

    /// <summary>Plugin-tick poll for a pending describe (no appraisal-arrived event in the SDK).</summary>
    private void TickItemInfo()
    {
        int id = _itemInfoPendingId;
        if (id == 0) return;
        bool ready = Host.HasAppraisalData(unchecked((uint)id));
        bool timedOut = Environment.TickCount64 - _itemInfoPendingSince > ItemInfoIdTimeoutMs;
        if (!ready && !timedOut) return;
        _itemInfoPendingId = 0;
        PrintItemInfo(id, identified: ready);
    }

    private void PrintItemInfo(int itemId, bool identified)
    {
        WorldObject? wo = _objectCache?[itemId];
        if (wo == null) { ChatLine($"[RynthAi] Item 0x{(uint)itemId:X8} is not in the object cache."); return; }

        var settings = _dashboard?.Settings;
        var options = new MagItemInfoOptions(
            ShowValueAndBurden: settings?.ItemInfoShowValueBurden ?? false,
            VerboseSpells:      settings?.ItemInfoVerboseSpells ?? false);

        string line;
        try { line = MagItemDescriber.Describe(new CacheItemPropertySource(Host, wo), options); }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.General, ex, $"item info 0x{(uint)itemId:X8}");
            ChatLine($"[RynthAi] Item info failed for {wo.Name}: {ex.Message}");
            return;
        }
        if (!identified) line += " (not identified — stats may be incomplete)";
        ChatLine("[RynthAi] " + line);
    }
}

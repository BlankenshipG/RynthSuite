using System;
using System.Text;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.ItemInfo;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra iteminfo — Mag-style one-line item info (ratings, damage, spells, slayer, imbues …),
/// ported from UtilityBelt ub-IT / Mag-Tools ItemInfo. Partial class of RynthAiPlugin.
///
///   /ra iteminfo                      describe the selected item (IDs it first when needed)
///   /ra iteminfo on|off               describe every item you select
///   /ra iteminfo settings             open/close the Item Info settings window
///   /ra iteminfo fields               list fields and ratings with their on/off state
///   /ra iteminfo field &lt;name&gt; on|off  show/hide one field (ratings, slayer, spells, …)
///   /ra iteminfo rating &lt;tag&gt; on|off  show/hide one rating (D, CD, CDR, …)
///   /ra iteminfo value on|off         append value + burden
///   /ra iteminfo verbose on|off       list every spell instead of Mag's filter
///   /ra iteminfo reset                restore defaults
/// Every option is also editable in the "RynthAi Item Info" window (LegacyItemInfoUi).
/// </summary>
public sealed partial class RynthAiPlugin
{
    // One pending describe at a time: the newest request replaces the previous one.
    private int  _itemInfoPendingId;
    private long _itemInfoPendingSince;

    /// <summary>Character settings when loaded, else null (commands then use defaults / refuse edits).</summary>
    private MagItemInfoSettings? ItemInfoSettings => _dashboard?.Settings.ItemInfoSettings;

    private void HandleItemInfoCommand(string[] parts)
    {
        var s = ItemInfoSettings;
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : string.Empty;
        string arg3 = parts.Length >= 4 ? parts[3] : string.Empty;
        string arg4 = parts.Length >= 5 ? parts[4] : string.Empty;

        if (sub.Length == 0)
        {
            PrintSelectedItemInfo();
            return;
        }
        if (s == null) { ChatLine("[RynthAi] Settings not ready (log in first)."); return; }

        switch (sub)
        {
            case "settings":
            case "gui":
            case "window":
                s.ShowWindow = !s.ShowWindow;
                ChatLine($"[RynthAi] Item info window {(s.ShowWindow ? "opened" : "closed")}.");
                return;

            case "value":
                if (ParseOnOff(arg3) is bool v) s.ShowValueAndBurden = v;
                ChatLine($"[RynthAi] Item info value/burden: {OnOff(s.ShowValueAndBurden)}");
                return;

            case "verbose":
                if (ParseOnOff(arg3) is bool all)
                    s.SpellMode = all ? MagItemInfoSettings.SpellModeAll : MagItemInfoSettings.SpellModeMag;
                ChatLine($"[RynthAi] Item info all spells: {OnOff(s.SpellMode == MagItemInfoSettings.SpellModeAll)}");
                return;

            case "field":
                if (!MagItemInfoCatalog.TryFindField(arg3, out MagItemInfoField field))
                {
                    ChatLine($"[RynthAi] Unknown field '{arg3}'. /ra iteminfo fields lists them.");
                    return;
                }
                if (ParseOnOff(arg4) is bool fieldOn) s.SetHidden(field, !fieldOn);
                ChatLine($"[RynthAi] Item info field {field}: {OnOff(!s.IsHidden(field))}");
                return;

            case "rating":
                int r = MagItemInfoCatalog.FindRating(arg3);
                if (r < 0)
                {
                    ChatLine($"[RynthAi] Unknown rating '{arg3}'. /ra iteminfo fields lists them.");
                    return;
                }
                if (ParseOnOff(arg4) is bool ratingOn) s.SetRatingHidden(r, !ratingOn);
                var rating = MagItemInfoCatalog.Ratings[r];
                ChatLine($"[RynthAi] Item info rating {rating.Tag} ({rating.Name}): {OnOff(!s.IsRatingHidden(r))}");
                return;

            case "fields":
                ListItemInfoFields(s);
                return;

            case "reset":
                s.ResetToDefaults();
                ChatLine("[RynthAi] Item info settings reset to defaults.");
                return;
        }

        if (ParseOnOff(sub) is bool onSelect)
        {
            s.OnSelect = onSelect;
            ChatLine($"[RynthAi] Item info on select: {OnOff(onSelect)}");
            return;
        }
        ChatLine("[RynthAi] Usage: /ra iteminfo [on|off|settings|fields|field <name> on|off|rating <tag> on|off|value on|off|verbose on|off|reset]");
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    /// <summary>"/ra iteminfo fields" — current state of every field and rating.</summary>
    private void ListItemInfoFields(MagItemInfoSettings s)
    {
        var sb = new StringBuilder("[RynthAi] Fields: ");
        bool first = true;
        foreach (var f in MagItemInfoCatalog.Fields)
        {
            if (!first) sb.Append(", ");
            sb.Append(f.Field).Append(s.IsHidden(f.Field) ? " off" : " on");
            first = false;
        }
        ChatLine(sb.ToString());

        sb.Clear().Append("[RynthAi] Ratings: ");
        var ratings = MagItemInfoCatalog.Ratings;
        for (int i = 0; i < ratings.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ratings[i].Tag).Append(s.IsRatingHidden(i) ? " off" : " on");
        }
        ChatLine(sb.ToString());
    }

    /// <summary>/ra iteminfo and the window's "Print to chat" button.</summary>
    private void PrintSelectedItemInfo()
    {
        if (!Host.HasGetSelectedItemId) { ChatLine("[RynthAi] Host does not expose the selected item."); return; }
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) { ChatLine("[RynthAi] No item selected. Click an item first."); return; }
        QueueItemInfo(unchecked((int)selected), requestId: true);
    }

    /// <summary>On-select class filters from the Item Info window.</summary>
    private static bool ItemInfoWantsClass(MagItemInfoSettings s, AcObjectClass cls) => cls switch
    {
        AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon or AcObjectClass.WandStaffOrb => s.OnSelectWeapons,
        AcObjectClass.Armor or AcObjectClass.Clothing => s.OnSelectArmor,
        AcObjectClass.Jewelry => s.OnSelectJewelry,
        _ => s.OnSelectOther,
    };

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
        long timeoutMs = (long)((ItemInfoSettings?.IdTimeoutSec ?? 3f) * 1000f);
        bool timedOut = Environment.TickCount64 - _itemInfoPendingSince > timeoutMs;
        if (!ready && !timedOut) return;
        _itemInfoPendingId = 0;
        PrintItemInfo(id, identified: ready);
    }

    /// <summary>Builds the line (no prefix) or returns null with <paramref name="error"/> set.</summary>
    private string? BuildItemInfoLine(int itemId, out string? error)
    {
        error = null;
        WorldObject? wo = _objectCache?[itemId];
        if (wo == null) { error = $"Item 0x{(uint)itemId:X8} is not in the object cache."; return null; }
        try
        {
            return MagItemDescriber.Describe(new CacheItemPropertySource(Host, wo), MagItemInfoOptions.From(ItemInfoSettings));
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.General, ex, $"item info 0x{(uint)itemId:X8}");
            error = $"Item info failed for {wo.Name}: {ex.Message}";
            return null;
        }
    }

    private void PrintItemInfo(int itemId, bool identified)
    {
        string? line = BuildItemInfoLine(itemId, out string? error);
        if (line == null) { ChatLine("[RynthAi] " + error); return; }
        if (!identified) line += " (not identified — stats may be incomplete)";

        var s = ItemInfoSettings;
        Host.WriteToChat((s?.Prefix ?? "[RynthAi] ") + line, s?.ChatType ?? 1);
    }

    /// <summary>Item Info window preview: the selected item's line, or null when nothing is selected.</summary>
    private string? DescribeSelectedItemForPreview()
    {
        if (!Host.HasGetSelectedItemId) return null;
        uint selected = Host.GetSelectedItemId();
        if (selected == 0) return null;
        int id = unchecked((int)selected);
        string? line = BuildItemInfoLine(id, out string? error);
        if (line == null) return error;
        if (Host.HasHasAppraisalData && !Host.HasAppraisalData(selected))
            line += " (not identified yet — Print to chat IDs it)";
        return line;
    }

    /// <summary>Item Info window "Test" button: sample line in the chosen chat type.</summary>
    private void TestItemInfoChatType(int chatType)
    {
        string prefix = ItemInfoSettings?.Prefix ?? "[RynthAi] ";
        Host.WriteToChat($"{prefix}Item info chat test (type {chatType})", chatType);
    }
}

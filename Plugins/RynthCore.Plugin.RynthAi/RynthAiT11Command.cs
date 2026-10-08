using System;
using System.Globalization;
using System.Linq;
using RynthCore.Loot.T11;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// /ra t11 [augs &lt;n|auto&gt; | refresh | item [0xId]]: T11 loot support.
    /// No argument shows the wield counters the loot rules use; "augs" sets or clears the
    /// item-aug override; "refresh" asks the server for "/aug"; "item" shows how the selected
    /// item's T11 text was read and the values its virtual loot keys return.
    /// </summary>
    private void HandleT11Command(string[] parts)
    {
        string sub = parts.Length >= 3 ? parts[2].ToLowerInvariant() : string.Empty;
        switch (sub)
        {
            case "":
            case "status":
                PrintT11Status();
                break;
            case "augs":
                SetT11ItemAugOverride(parts.Length >= 4 ? parts[3] : string.Empty);
                break;
            case "refresh":
                if (!Host.HasInvokeChatParser) { ChatLine("[RynthAi] This engine can't send chat commands."); break; }
                Host.InvokeChatParser("/aug");
                ChatLine("[RynthAi] T11: sent /aug - the counters update from its reply.");
                break;
            case "item":
                PrintT11Item(parts.Length >= 4 ? parts[3] : null);
                break;
            default:
                ChatLine("[RynthAi] Usage: /ra t11 [augs <n|auto> | refresh | item [0xId]]");
                break;
        }
    }

    /// <summary>The counters the "T11: Can Wield" loot key compares wield gates against.</summary>
    private void PrintT11Status()
    {
        T11PlayerAugs p = T11ItemSupport.Player;
        int saved = _dashboard?.Settings.T11ItemAugsOverride ?? -1;
        string updated = p.UpdatedUtc is DateTime t ? t.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "never";
        ChatLine($"[RynthAi] === T11 loot (last /aug read: {updated}) ===");
        foreach ((T11Counter counter, string name) in T11Catalog.CounterNames)
        {
            long? v = p.Get(counter);
            string source = counter == T11Counter.ItemAugmentations && saved >= 0 ? " (override)" : string.Empty;
            ChatLine($"[RynthAi]   {name}: {(v.HasValue ? v.Value.ToString("N0", CultureInfo.CurrentCulture) : "unknown")}{source}");
        }
        if (p.UpdatedUtc == null && saved < 0)
            ChatLine("[RynthAi]   Unknown counters make \"T11: Can Wield\" -1. Use /ra t11 refresh, or /ra t11 augs <n>.");
    }

    /// <summary>/ra t11 augs &lt;n|auto&gt;: saves the item-aug override (-1 = read from /aug).</summary>
    private void SetT11ItemAugOverride(string arg)
    {
        if (_dashboard == null) { ChatLine("[RynthAi] Settings aren't loaded yet."); return; }
        int value;
        if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase) || arg == "-1")
            value = -1;
        else if (!int.TryParse(arg.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            ChatLine("[RynthAi] Usage: /ra t11 augs <n|auto>  (n = your item augmentation count)");
            return;
        }
        _dashboard.Settings.T11ItemAugsOverride = value;
        _dashboard.SaveSettings();
        T11ItemSupport.SyncOverride(value);
        ChatLine(value < 0
            ? "[RynthAi] T11: item augmentations now come from /aug."
            : $"[RynthAi] T11: item augmentations fixed at {value:N0} for loot rules.");
    }

    /// <summary>/ra t11 item [0xId]: the parsed T11 text of an item (selected item by default).</summary>
    private void PrintT11Item(string? idArg)
    {
        uint id = 0;
        if (!string.IsNullOrEmpty(idArg))
            uint.TryParse(idArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idArg[2..] : idArg,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        if (id == 0 && Host.HasGetSelectedItemId) id = Host.GetSelectedItemId();
        if (id == 0) { ChatLine("[RynthAi] T11: select an item first (or pass 0xId)."); return; }

        int iid = unchecked((int)id);
        WorldObject item = _objectCache?[iid]
            ?? new WorldObject(iid, Host.TryGetObjectName(id, out string n) ? n : string.Empty) { Cache = _objectCache };
        T11ItemInfo info = T11ItemSupport.Get(item);
        if (!info.IsT11)
        {
            bool appraised = !Host.HasHasAppraisalData || Host.HasAppraisalData(id);
            ChatLine(appraised
                ? $"[RynthAi] T11: {item.Name} is not a T11 item."
                : $"[RynthAi] T11: {item.Name} isn't appraised yet - ID it and try again.");
            return;
        }

        ChatLine($"[RynthAi] === T11: {item.Name} (0x{id:X8}) ===");
        ChatLine($"[RynthAi]   Tier {(info.TierIsExact ? info.Tier.ToString(CultureInfo.InvariantCulture) : "~" + info.Tier.ToString(CultureInfo.InvariantCulture))}"
            + (info.Grade != null ? $", grade {info.Grade} ({info.DamagePercent}% of max damage)" : string.Empty)
            + (info.WeaponQuality >= 0 ? $", quality {info.WeaponQuality}/1000" : string.Empty)
            + (info.GearGrade != null ? $", gear grade {info.GearGrade} ({info.GearGradeLines} lines)" : string.Empty)
            + (info.ZoneLocked ? ", zone locked" : string.Empty));
        if (info.PropertySlots >= 0 || info.Tainted)
            ChatLine("[RynthAi]   Properties "
                + (info.PropertySlots < 0 ? "?" : info.PropertySlotCap > 0 ? $"{info.PropertySlots} of {info.PropertySlotCap}" : info.PropertySlots.ToString(CultureInfo.InvariantCulture))
                + (info.Tainted ? ", tainted (bags no longer work)" : string.Empty));
        foreach (T11WieldGate g in info.WieldGates)
            ChatLine($"[RynthAi]   Wield requires {g.Amount:N0} {T11Catalog.CounterName(g.Counter)}");
        foreach (T11Modifier m in info.Modifiers)
        {
            string band = m.Min.HasValue && m.Max.HasValue ? $" [{m.Min}-{m.Max}] roll {m.RollPercent}%" : string.Empty;
            string marks = (m.Tinkered != 0 ? $" ({m.Tinkered:+#;-#} tinkered)" : string.Empty)
                + (m.BuiltIn ? " (built-in)" : string.Empty) + (m.Locked ? " (locked)" : string.Empty);
            ChatLine($"[RynthAi]   Mod {m.Name} {(m.IsSlotSpecial ? "(special)" : "+" + m.Value)}{band}{marks}");
        }
        foreach ((string name, double chance) in info.Procs)
            ChatLine($"[RynthAi]   Cast on strike: {name} ({chance.ToString("0.#", CultureInfo.CurrentCulture)}%)");

        int canWield = item.Values(T11Keys.CanWield, -1);
        ChatLine($"[RynthAi]   Can wield: {(canWield == 1 ? "yes" : canWield == 0 ? "no" : "unknown (no /aug read, or not appraised)")}"
            + $", modifier ratings {info.ModifierRatingTotal}, average roll {info.AverageRollPercent}%");
        string keys = string.Join(", ", T11Keys.Names()
            .Where(k => k.Id < T11Keys.ModifierBase)
            .Select(k => $"{k.Name.Replace("T11: ", string.Empty)}={item.Values(k.Id, 0)}"));
        ChatLine($"[RynthAi]   Loot keys: {keys}");
    }
}

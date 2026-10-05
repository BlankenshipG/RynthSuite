using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Keep # (KeepUpTo) arithmetic, split out of <c>RynthAiPlugin.UnderKeepCap</c> so it can be
/// tested offline. The plugin passes in the live pack snapshot and its per-corpse "approved
/// but not in the pack yet" table; nothing here touches the host.
/// </summary>
internal static class LootKeepCap
{
    /// <summary>How much one item counts toward a cap: its stack size, never less than 1.</summary>
    internal static int StackOf(WorldObject item) => Math.Max(1, item.Values(LongValueKey.StackCount, 1));

    /// <summary>
    /// How many of what a Keep # rule matches the player already has: whole stacks of every
    /// named pack item the rule matches (a matcher that throws counts as no match), plus items
    /// approved on this corpse that are not in the pack yet. An approved item that is already in
    /// the pack snapshot, or that no longer exists (a looted stack merged into a pack stack and
    /// was deleted), is not counted twice.
    /// </summary>
    internal static int CountHave(
        IEnumerable<WorldObject>? inventory,
        Func<WorldObject, bool> matches,
        IReadOnlyDictionary<int, int> pending,
        Func<int, bool> stillExists)
    {
        int have = 0;
        var liveIds = new HashSet<int>();
        if (inventory != null)
        {
            foreach (var inv in inventory)
            {
                liveIds.Add(inv.Id);
                if (string.IsNullOrEmpty(inv.Name)) continue;
                bool m;
                try { m = matches(inv); } catch { m = false; }
                if (m) have += StackOf(inv);
            }
        }
        foreach (var kv in pending)
            if (!liveIds.Contains(kv.Key) && stillExists(kv.Key)) have += kv.Value;
        return have;
    }

    /// <summary>True while the player holds fewer than <paramref name="cap"/>; a cap of 0 or less never loots.</summary>
    internal static bool UnderCap(int have, int cap) => have < cap;
}

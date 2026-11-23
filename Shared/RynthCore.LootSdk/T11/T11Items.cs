using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Loot.T11;

/// <summary>
/// ACECustom's newer T11-era loot items (2026-11-11, upstream to 8d2460ff2), for the "T11: Item" loot keys: what an item is
/// (<see cref="T11Keys.ItemKind"/>), the tier it belongs to (<see cref="T11Keys.ItemTier"/>) and the coins a Charm Infusion
/// Gem puts in (<see cref="T11Keys.InfusionCoins"/>). Known by WCID, an item bounty by its description (the server writes
/// its terms there: "Kill 250 Banderlings in Tier 15. ... Use it on a ring or bracelet you have to start it").
/// </summary>
public static class T11Items
{
    public const int None = 0, ItemBounty = 1, SchoolGem = 2, InfusionGem = 3, LevelledCharm = 4, BountyRewardToken = 5,
        EventLumToken = 6, UniversalQuiver = 7;

    /// <summary>The kinds for the editor's picker ("is" a kind).</summary>
    public static readonly (int Id, string Name)[] Kinds =
    {
        (ItemBounty, "Item bounty"), (SchoolGem, "School gem"), (InfusionGem, "Charm Infusion Gem"),
        (LevelledCharm, "Levelled charm"), (BountyRewardToken, "Bounty Reward Token"), (EventLumToken, "Event Lum Token"),
        (UniversalQuiver, "Universal Quiver Charm"),
    };

    /// <summary>PropertyInt EarnedGemTier on a school gem (the tier it was earned at).</summary>
    public const int PropEarnedGemTier = 50651;

    /// <summary>Charm Infusion Gems (777700089-097) and the coins one gem puts into a levelled charm.</summary>
    public static readonly IReadOnlyDictionary<uint, int> InfusionGems = new Dictionary<uint, int>
    {
        [777700089] = 1, [777700090] = 5, [777700091] = 10, [777700092] = 25, [777700093] = 50,
        [777700094] = 100, [777700095] = 500, [777700096] = 1_000, [777700097] = 5_000,
    };

    /// <summary>The school gems (+1 a school past the cap, T15-T25): Battlemage's Wrath, Nether Veil, Crashing Steel, True Shot.</summary>
    public static readonly uint[] SchoolGems = { 777700052, 777700057, 777700062, 777700067 };

    /// <summary>What the item is (0 for none of these). <paramref name="longDesc"/> only matters for an item bounty.</summary>
    public static int KindOf(uint wcid, string? longDesc)
    {
        if (InfusionGems.ContainsKey(wcid)) return InfusionGem;
        if (Array.IndexOf(SchoolGems, wcid) >= 0) return SchoolGem;
        if (wcid is >= 777700035 and <= 777700040) return LevelledCharm;
        if (wcid is >= 777708001 and <= 777708010) return BountyRewardToken;
        if (wcid is 99980076 or 99980078 or 99980079) return EventLumToken;
        if (wcid == 78780032) return UniversalQuiver;
        if (!string.IsNullOrEmpty(longDesc) && longDesc.Contains("Use it on a ring or bracelet", StringComparison.OrdinalIgnoreCase)) return ItemBounty;
        return None;
    }

    private static readonly Regex BountyTier = new(@"\bin Tier (\d+)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The tier the item belongs to: an item bounty's ("in Tier 15"; 0 for a region bounty, Valley of Death, Thaelaryn
    /// Island), a school gem's earned tier; 0 for anything else.
    /// </summary>
    public static int TierOf(int kind, string? longDesc, int earnedGemTier)
    {
        if (kind == SchoolGem) return Math.Max(0, earnedGemTier);
        if (kind != ItemBounty || string.IsNullOrEmpty(longDesc)) return 0;
        var m = BountyTier.Match(longDesc);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
    }

    /// <summary>The coins one Charm Infusion Gem puts in; 0 for anything else.</summary>
    public static int CoinsOf(uint wcid) => InfusionGems.TryGetValue(wcid, out int c) ? c : 0;
}

using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// 2026-11-15, Silentkelpie: "is the any slayer rule working for looting". ACECustom never sends the slayer bonus
// (float 138), only "- Shadow Slayer: 1.5x Damage" in the weapon's text, so "Slayer damage bonus at least 1.01"
// never matched. The slayer type (int 166, "any slayer") always worked.
internal static partial class Program
{
    private static void RunSlayerTextTests()
    {
        System.Console.WriteLine("\n-- Slayer: the damage bonus from the weapon's text --");
        Eq(SlayerText.BonusOf("Property Details:\n- Weapon Grade: C+ (70% of max damage)\n- Shadow Slayer: 1.5x Damage\n- Biting Strike: +35% Crit Chance\n"),
            1.5, "slayer: the bonus from a Property Details line");
        Eq(SlayerText.BonusOf("- Banderling Slayer: 2.1x Damage"), 2.1, "slayer: two words before Slayer");
        Eq(SlayerText.BonusOf(null, "- All Creatures Slayer: 1.25x Damage"), 1.25, "slayer: All Creatures, from the second text");
        Eq(SlayerText.BonusOf("- Undead Slayer: 1.3x Damage\n- Olthoi Slayer: 1.8x Damage"), 1.8, "slayer: the best of two");
        Eq(SlayerText.BonusOf("A fine sword.", null, ""), 0.0, "slayer: no slayer line, 0");
        Eq(SlayerText.BonusOf("- Biting Strike: +35% Crit Chance"), 0.0, "slayer: other card lines, 0");

        // The rule reader: float 138 missing (as on ACECustom) reads the text's bonus.
        var t11 = T11ItemInfo.Parse("Khanda-handled Mace",
            "Property Details:\n- Weapon Grade: C+ (70% of max damage)\n- Properties: 2 of 5\n- Shadow Slayer: 1.5x Damage\n", null);
        var f = new LootItemFacts { Name = "Khanda-handled Mace", ObjectClass = 1, T11 = t11 };
        f.Ints[166] = 22;
        Eq(f.Double(SlayerText.SlayerDamageBonusKey), 1.5, "slayer: Double(138) falls back to the text");
        f.Doubles[138] = 1.7;
        Eq(f.Double(SlayerText.SlayerDamageBonusKey), 1.7, "slayer: a sent value wins");
        f.Doubles.Remove(138);

        // The profile's rule shape: any slayer AND Slayer damage bonus at least 1.01.
        var conds = new List<VTankLootCondition>
        {
            new(VTankNodeTypes.LongValKeyGE, "0", new[] { "1", "166" }),
            new(VTankNodeTypes.DoubleValKeyGE, "0", new[] { "1.01", "138" }),
        };
        Eq(LootFactsMatcher.Match(conds, f, identified: true), LootFactsVerdict.Match, "slayer: 'any slayer + bonus >= 1.01' matches a Shadow Slayer");
        var plain = new LootItemFacts { Name = "Spada", ObjectClass = 1, T11 = T11ItemInfo.Parse("Spada", "Property Details:\n- Weapon Grade: B (80% of max damage)\n", null) };
        Eq(LootFactsMatcher.Match(conds, plain, identified: true), LootFactsVerdict.NoMatch, "slayer: and not a weapon without one");
    }
}

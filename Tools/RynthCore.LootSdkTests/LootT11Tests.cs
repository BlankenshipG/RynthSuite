using System;
using System.Linq;
using RynthCore.Loot.Editing;
using RynthCore.Loot.T11;

namespace RynthCore.LootSdkTests;

// T11 (ACECustom tier 11+) loot support: the item-text parser against the server's exact
// strings, tier estimates, the "/aug" reply reader, wield checks, virtual keys, the stripped
// wield/rating fallbacks, and the editor's key names and grade table.
internal static partial class Program
{
    // A T13 weapon as ACECustom appraises it: Property Details then Modifiers in LongDesc.
    private const string T11WeaponLongDesc =
        "A finely made sword.\n\n" +
        "Property Details:\n" +
        "- Weapon Grade: B+ (86% of max damage)\n" +
        "- Wield requires: 3,000 Item Augmentations\n" +
        "- Wield requires: 25 Crashing Steel\n" +
        "- Cast on Strike: Force Arc (13% proc chance)\n" +
        "\n" +
        "Modifiers:\n" +
        "- Damage Rating +41 [14-69]\n" +
        "- Crit Damage Rating +10 [5-15]\n" +
        "- Pct HP Damage 2%\n" +
        "- Zone Locked (Power Reduced)\n";

    // T16 armor: gates in LongDesc without a dash, Modifiers in the Use string.
    private const string T11ArmorLongDesc =
        "Sturdy plate.\n" +
        "Wield requires: 4.000 Item Augmentations\n" +
        "Wield requires: 500 Triune Weave\n";

    private const string T11ArmorUse =
        "Modifiers:\n" +
        "- Damage Resist +8 [2-12]\n" +
        "- Max Health +120 [40-200]\n" +
        "- Max Health Pct +2% [1-4]\n" +
        "- Modifier 60 +3 [1-5]\n";

    private static void RunT11Tests()
    {
        Console.WriteLine("\n-- T11 loot --");
        TestT11WeaponParse();
        TestT11ArmorParse();
        TestT11NonT11();
        TestT11Tier();
        TestT11AugReport();
        TestT11Keys();
        TestT11Editor();
    }

    private static void TestT11WeaponParse()
    {
        T11ItemInfo w = T11ItemInfo.Parse("T11 - Tachi", T11WeaponLongDesc, null);
        Check(w.IsT11 && w.HasNamePrefix, "weapon: T11 by prefix");
        Eq(w.Grade, "B+", "weapon: grade label");
        Eq(w.GradeRank, 12, "weapon: grade rank B+ = 12");
        Eq(w.DamagePercent, 86, "weapon: damage % of max");
        Eq(w.WieldItemAugs, 3000, "weapon: item-aug gate with group separator");
        Eq(w.GateAmount(T11Counter.CrashingSteel), 25, "weapon: charm gate");
        Eq(w.Procs.Count, 1, "weapon: one cast on strike");
        Eq(w.Procs.Count > 0 ? w.Procs[0].Name : "", "Force Arc", "weapon: proc name");
        Eq(w.Procs.Count > 0 ? w.Procs[0].ChancePercent : 0, 13.0, "weapon: proc chance");
        Check(w.ZoneLocked, "weapon: zone locked line inside Modifiers");
        Eq(w.Modifiers.Count, 3, "weapon: zone lock line is not a modifier");
        T11Modifier dr = w.Modifiers.First(m => m.Key == 28);
        Eq(dr.Value, 41, "weapon: Damage Rating value");
        Eq((dr.Min, dr.Max), ((int?)14, (int?)69), "weapon: Damage Rating band");
        Eq(dr.RollPercent, 49, "weapon: Damage Rating roll (41-14)/(69-14)");
        Check(w.Modifiers.Any(m => m.Key == 44 && m.IsSlotSpecial), "weapon: slot special without a band");
        Check(w.HasSlotSpecial, "weapon: HasSlotSpecial");
        Eq(w.RatingFromModifiers(370), 41, "weapon: 370 from Damage Rating");
        Eq(w.RatingFromModifiers(374), 10, "weapon: 374 from Crit Damage Rating");
        Eq(w.ModifierRatingTotal, 51, "weapon: modifier rating total");
        Eq(w.EstimatedTier, 13, "weapon: 3,000 gate = T13");
        Eq(w.BestRollPercent, 50, "weapon: best roll (Crit Damage 10 in 5-15)");
    }

    private static void TestT11ArmorParse()
    {
        T11ItemInfo a = T11ItemInfo.Parse("T11 - Celdon Breastplate", T11ArmorLongDesc, T11ArmorUse);
        Check(a.IsT11, "armor: T11");
        Eq(a.Grade, null, "armor: no weapon grade");
        Eq(a.WieldItemAugs, 4000, "armor: dot group separator, no dash");
        Eq(a.GateAmount(T11Counter.TriuneWeave), 500, "armor: Triune Weave gate");
        Eq(a.EstimatedTier, 16, "armor: 500 Triune Weave = T16");
        Eq(T11Keys.TryGetValue(a, T11Keys.EstimatedTier, null, out int at) ? at : 0, 16, "armor: tier key reads T16");
        Eq(a.Modifiers.Count, 4, "armor: modifiers from Use");
        Check(a.Modifiers.Any(m => m.Key == 47 && m.Value == 2), "armor: Max Health Pct is not Max Health");
        Check(a.Modifiers.Any(m => m.Key == 19 && m.Value == 120), "armor: Max Health");
        Check(a.Modifiers.Any(m => m.Key == 60 && m.Name == "Modifier 60" && m.Value == 3), "armor: unknown server key kept");
        Eq(a.RatingFromModifiers(371), 8, "armor: 371 from Damage Resist");
        Eq(a.RatingFromModifiers(379), 120, "armor: 379 from Max Health");
        Eq(a.ModifierRatingTotal, 128, "armor: rating total 371 + 379");
    }

    private static void TestT11NonT11()
    {
        Check(!T11ItemInfo.Parse("Tachi", "A finely made sword.", "").IsT11, "plain item is not T11");
        Check(!T11ItemInfo.Parse(null, null, null).IsT11, "no text is not T11");
        Check(!T11ItemInfo.Parse("Wand", "- Cast on Strike: Bolt (5% proc chance)", null).IsT11, "a proc alone doesn't mark T11");
        Check(T11ItemInfo.Parse("T11 - Robe", null, null).IsT11, "unappraised T11 name still T11");
        Eq(T11ItemInfo.Parse("T11 - Robe", null, null).EstimatedTier, 11, "unappraised T11 = tier 11");
        Check(!T11ItemInfo.Parse("T11 - Robe", null, null).HasText, "unappraised T11 has no text");
        Eq(T11Keys.TryGetValue(T11ItemInfo.Parse("T11 - Robe", null, null), T11Keys.CanWield, new T11PlayerAugs(), out int cw) ? cw : 99, -1,
            "unappraised T11: Can Wield unknown");
        Eq(T11Keys.TryGetValue(T11ItemInfo.Parse("T11 - Robe", "A robe.", null), T11Keys.CanWield, null, out int cw2) ? cw2 : 99, 1,
            "appraised T11 without a gate: wieldable");
        Check(!T11ItemInfo.Parse("x", "Modifiers:\n- ", "\n\n- [").IsT11, "malformed text doesn't throw or mark");
    }

    private static void TestT11Tier()
    {
        Eq(T11Catalog.EstimateTier(0, false), 0, "tier: nothing");
        Eq(T11Catalog.EstimateTier(2000, true), 11, "tier: 2,000 = T11");
        Eq(T11Catalog.EstimateTier(2500, true), 12, "tier: 2,500 = T12");
        Eq(T11Catalog.EstimateTier(3500, true), 14, "tier: 3,500 = T14");
        Eq(T11Catalog.EstimateTier(9000, true), 15, "tier: capped at 15");

        // T16-T25: the item-aug gate is frozen at 4,000, the Triune Weave gate carries the tier.
        Eq(T11Catalog.EstimateTier(4000, 0, true), 15, "tier: 4,000 without Triune = T15");
        Eq(T11Catalog.EstimateTier(3000, 0, true), 13, "tier: no Triune falls back to the item-aug gate");
        Eq(T11Catalog.EstimateTier(4000, 500, true), 16, "tier: 500 Triune = T16");
        Eq(T11Catalog.EstimateTier(4000, 1500, true), 18, "tier: 1,500 Triune = T18");
        Eq(T11Catalog.EstimateTier(4000, 5000, true), 25, "tier: 5,000 Triune = T25");
        Eq(T11Catalog.EstimateTier(4000, 9000, true), T11Catalog.MaxTier, "tier: capped at T25");
        Eq(T11Catalog.EstimateTier(4000, 250, true), 16, "tier: any Triune gate is at least T16");
        Eq(T11Catalog.EstimateTier(0, 0, false), 0, "tier: nothing (three-argument)");

        // A T25 weapon as appraised: item augs frozen at 4,000, Triune and family charm 5,000 each.
        T11ItemInfo t25 = T11ItemInfo.Parse("T11 - Tachi",
            "Property Details:\n" +
            "- Weapon Grade: A (95% of max damage)\n" +
            "- Wield requires: 4,000 Item Augmentations\n" +
            "- Wield requires: 5,000 Triune Weave\n" +
            "- Wield requires: 5,000 Crashing Steel\n", null);
        Eq(t25.EstimatedTier, 25, "tier: T25 weapon from its Triune gate");
        Eq(t25.GateAmount(T11Counter.CrashingSteel), 5000, "tier: T25 weapon charm gate");
    }

    private static void TestT11AugReport()
    {
        var p = new T11PlayerAugs();
        var r = new T11AugReportParser();
        Check(!r.Feed("Item:2,500", 0, p), "aug: Item line before the header ignored");
        Check(!r.Feed("Advanced Augmentation Levels:", 1000, p), "aug: header opens the window");
        Check(r.Feed("Item:2,500", 1100, p), "aug: Item line read");
        Check(r.Feed("Triune Weave: 3 (adds 3% damage)", 1200, p), "aug: Triune Weave read");
        Check(r.Feed("Crashing Steel Charm: 20", 1300, p), "aug: charm read");
        Check(!r.Feed("Item:9999", 1300 + T11AugReportParser.WindowMs + 1, p), "aug: window closes");
        Eq(p.Get(T11Counter.ItemAugmentations), (long?)2500, "aug: item count");
        Eq(p.Get(T11Counter.TriuneWeave), (long?)3, "aug: triune count");
        Eq(p.Get(T11Counter.NetherVeil), null, "aug: unreported counter unknown");

        T11ItemInfo weapon = T11ItemInfo.Parse("T11 - Tachi", T11WeaponLongDesc, null);
        Eq(p.CanWield(weapon), 0, "wield: 2,500 < 3,000");
        p.ItemAugOverride = 3000;
        Eq(p.CanWield(weapon), 0, "wield: crashing steel 20 < 25");
        p.Set(T11Counter.CrashingSteel, 25);
        Eq(p.CanWield(weapon), 1, "wield: override + charm met");
        p.Clear();
        Eq(p.Get(T11Counter.ItemAugmentations), (long?)3000, "aug: Clear keeps the override");
        Eq(p.CanWield(weapon), -1, "wield: charm unknown after Clear");
    }

    private static void TestT11Keys()
    {
        T11ItemInfo w = T11ItemInfo.Parse("T11 - Tachi", T11WeaponLongDesc, null);
        int V(int key, T11PlayerAugs? pl = null) => T11Keys.TryGetValue(w, key, pl, out int v) ? v : int.MinValue;
        Eq(V(T11Keys.IsT11), 1, "key: IsT11");
        Eq(V(T11Keys.EstimatedTier), 13, "key: tier");
        Eq(V(T11Keys.WeaponGrade), 12, "key: grade");
        Eq(V(T11Keys.DamagePercent), 86, "key: damage %");
        Eq(V(T11Keys.WieldItemAugs), 3000, "key: wield item augs");
        Eq(V(T11Keys.CanWield), -1, "key: can wield unknown without player");
        Eq(V(T11Keys.ModifierCount), 3, "key: modifier count");
        Eq(V(T11Keys.ModifierRatingTotal), 51, "key: modifier ratings");
        Eq(V(T11Keys.CastOnStrikeCount), 1, "key: procs");
        Eq(V(T11Keys.ZoneLocked), 1, "key: zone locked");
        Eq(V(T11Keys.ForModifier(28)), 41, "key: per-modifier Damage Rating");
        Eq(V(T11Keys.ForModifier(50)), 0, "key: absent modifier = 0");
        Eq(V(T11Keys.ModifierBase + 999), int.MinValue, "key: unknown modifier id unused");
        Eq(V(370), int.MinValue, "key: retail key isn't virtual");
        Check(T11Keys.IsVirtual(T11Keys.IsT11) && !T11Keys.IsVirtual(T11Keys.Base) && !T11Keys.IsVirtual(T11Keys.End), "key: block bounds");

        int S(T11ItemInfo item, int key) => T11Keys.TryGetStrippedValue(item, key, out int v) ? v : int.MinValue;
        Eq(S(w, 158), 13, "stripped: 158 = Int64 Property");
        Eq(S(w, 159), 9008, "stripped: 159 = item augs stat");
        Eq(S(w, 160), 3000, "stripped: 160 = gate");
        Eq(S(w, 370), 41, "stripped: 370 from modifier");
        Eq(S(w, 371), int.MinValue, "stripped: no modifier for 371");
        Eq(S(T11ItemInfo.None, 158), int.MinValue, "stripped: non-T11 gives nothing");
        Eq(S(T11ItemInfo.Parse("T11 - Robe", null, null), 160), int.MinValue, "stripped: no gate read yet");
        Check(T11Keys.IsStrippedKey(158) && T11Keys.IsStrippedKey(379) && !T11Keys.IsStrippedKey(161), "stripped: key set");
    }

    private static void TestT11Editor()
    {
        Check(LootRuleText.LongKeys.Any(k => k.Id == T11Keys.CanWield && k.Name == "T11: Can Wield"), "editor: Can Wield key named");
        Check(LootRuleText.LongKeys.Any(k => k.Id == T11Keys.ForModifier(28) && k.Name == "T11 Mod: Damage Rating"), "editor: per-modifier key named");
        Check(LootRuleText.LongKeys.Any(k => k.Id == 377), "editor: NetherResistRating listed");
        Eq(LootRuleText.LongKeys.Select(k => k.Id).Distinct().Count(), LootRuleText.LongKeys.Length, "editor: long key ids unique");
        Check(ReferenceEquals(LootRuleText.ValueTableFor(T11Keys.WeaponGrade), T11Catalog.Grades), "editor: grade value table");
        Eq(LootRuleText.ValueName(T11Keys.WeaponGrade, "12"), "B+", "editor: grade 12 named B+");
        Eq(LootRuleText.WieldRequirements.First(w => w.Id == 13).Name, "Int64 Property", "editor: wield requirement 13");
        Check(LootRuleText.StringKeys.Any(k => k.Id == 14) && LootRuleText.StringKeys.Any(k => k.Id == 16), "editor: Use and LongDesc string keys");
        Eq(T11Catalog.Grades.Select(g => g.Name).Distinct().Count(), T11Catalog.Grades.Length, "editor: grade names unique");
    }
}

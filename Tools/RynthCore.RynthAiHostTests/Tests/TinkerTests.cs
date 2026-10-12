using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.IltHub.Crafting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Crafting tab's tinkering (2026-10-12): ACE's retail chance (RecipeManager.GetTinkerChance),
// the planner's caps and order (imbue once, ILT no-slot salvage, Steel + Serpentine 10, Leather
// after the cap, rend last), bag matching / choice, and the universal Crafting tab id.
internal static class TinkerTests
{
    public static void Register(Runner r)
    {
        r.Add("tinker: retail difficulty and chance (ACE GetTinkerChance)", Chance);
        r.Add("tinker: slot cap, one imbue, attempts this run", SlotsAndImbue);
        r.Add("tinker: damage-type rend and rend last", Rend);
        r.Add("tinker: ILT weapon rows (no slot, Leather after the cap)", IltWeapon);
        r.Add("tinker: ILT jewelry flags and Steel + Serpentine cap", IltJewelry);
        r.Add("tinker: item rows (Moonstone, Leather, Sandstone)", ItemRows);
        r.Add("tinker: scan filters", Filters);
        r.Add("tinker: bag matching (full, Abyssal, Foolproof)", Bags);
        r.Add("tinker: bag choice by chance and minimum", BagChoice);
        r.Add("tinker: Crafting is a universal Mini Remote tab", Section);
    }

    private static IltTinkerBenchSettings Settings(params (string Key, int Count)[] counts)
    {
        var s = new IltTinkerBenchSettings { MaxTinks = 10 };
        foreach (var (k, n) in counts) s.Counts[k] = n;
        return s;
    }

    private static TinkerItemFacts Item(AcObjectClass oc, int tinks = 0, bool imbued = false, int damageType = 0,
                                        double work = 6, Func<int, int>? flags = null)
        => new() { Id = 1, Name = "Test Item", Class = oc, Tinks = tinks, Imbued = imbued, DamageType = damageType,
                   Workmanship = work, FlagInt = flags ?? (_ => 0) };

    private static List<int> Mats(List<TinkerStep> plan) => plan.Select(p => p.Material).ToList();

    /// <summary>A plan's materials as "a,b,c" (Check.Eq compares lists by reference).</summary>
    private static string Seq(List<TinkerStep> plan) => string.Join(",", Mats(plan));

    private static string Seq(params int[] ids) => string.Join(",", ids);

    private static void Chance()
    {
        // Steel (mod 12), item 5, bag 6 (>= item: wkMod 2): 60 + 120 - 28.8 = 151.2.
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Steel, 5, 6, 0), 151, "steel difficulty, first tink");
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Steel, 5, 6, 3), 241, "fourth tink: x1.6");
        // Gold (mod 10), item 10, bag 3 (< item: wkMod 1): 50 + 200 - 6 = 244.
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Gold, 10, 3, 0), 244, "gold, weaker bag");
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Iron, 5, 5, 10), int.MaxValue, "no 11th tink");
        Check.True(Math.Abs(TinkerMath.SkillChance(200, 200) - 0.5) < 1e-9, "skill = difficulty is 50%");
        Check.Eq(TinkerMath.Percent(TinkerMath.Chance(TinkerCatalog.Steel, 300, 5, 6, 0, false, false)), 99, "300 skill vs 151");
        double plain = TinkerMath.Chance(TinkerCatalog.BlackOpal, 250, 6, 6, 0, false, false);
        double imbue = TinkerMath.Chance(TinkerCatalog.BlackOpal, 250, 6, 6, 0, true, false);
        Check.True(Math.Abs(imbue - plain / 3) < 1e-9, "imbues are a third");
        Check.Eq(TinkerMath.Chance(TinkerCatalog.BlackOpal, 1, 10, 1, 9, true, true), 1.0, "foolproof always lands");
        Check.Eq(TinkerMath.SalvageMod(TinkerCatalog.RoseQuartz), 25.0, "gem cantrips are mod 25");
        Check.Eq(TinkerMath.SalvageMod(TinkerCatalog.WhiteQuartz), 20.0, "default mod 20");
        Check.Eq(TinkerMath.BagWorkmanship(600, 100), 6.0, "bag workmanship is the average");
    }

    private static void SlotsAndImbue()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Melee, false, null);
        var s = Settings(("r" + TinkerCatalog.Iron, 10), ("r" + TinkerCatalog.BlackOpal, 1), ("r" + TinkerCatalog.FireOpal, 1));
        var plan = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 6), s, rows);
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.BlackOpal), 1, "first imbue in order");
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.FireOpal), 0, "second imbue refused");
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.Iron), 3, "imbue takes a slot: 4 free - 1");
        Check.Eq(plan[0].Material, TinkerCatalog.BlackOpal, "imbue before stat tinks");

        var imbued = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 6, imbued: true), s, rows);
        Check.Eq(Mats(imbued).Count(m => m == TinkerCatalog.BlackOpal), 0, "already imbued");
        Check.Eq(imbued.Count, 4, "four Irons fill the free slots");

        var tried = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 0, imbued: true),
            Settings(("r" + TinkerCatalog.Iron, 3)), rows, k => k == "r" + TinkerCatalog.Iron ? 2 : 0);
        Check.Eq(tried.Count, 1, "two of three Irons already tried this run");
        Check.Eq(TinkerPlanner.Summary(imbued), "Iron x4", "summary groups repeats");
    }

    private static void Rend()
    {
        Check.Eq(TinkerCatalog.RendFor(3, false), TinkerCatalog.ImperialTopaz, "slash/pierce: slash");
        Check.Eq(TinkerCatalog.RendFor(3, true), TinkerCatalog.BlackGarnet, "slash/pierce: pierce");
        Check.Eq(TinkerCatalog.RendFor(4, false), TinkerCatalog.WhiteSapphire, "bludgeon");
        Check.Eq(TinkerCatalog.RendFor(16, false), TinkerCatalog.RedGarnet, "fire");
        Check.Eq(TinkerCatalog.RendFor(0x400, false), TinkerCatalog.Onyx, "nether");
        Check.Eq(TinkerCatalog.RendFor(0, false), 0, "no damage type, no rend");

        var rows = TinkerPlanner.Rows(TinkerBench.Melee, false, null);
        var s = Settings(("r" + TinkerCatalog.RendId, 1), ("r" + TinkerCatalog.Iron, 2));
        var first = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, damageType: 32), s, rows);
        Check.Eq(first[0].Material, TinkerCatalog.Emerald, "acid weapon: Emerald first");
        s.RendLast = true;
        var last = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, damageType: 32), s, rows);
        Check.Eq(last[^1].Material, TinkerCatalog.Emerald, "rend last");
        Check.Eq(last.Count, 3, "rend + 2 Iron");
    }

    private static void IltWeapon()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Melee, true, null);
        var s = Settings(("s" + TinkerCatalog.YellowGarnet, 1), ("r" + TinkerCatalog.Iron, 10), ("s" + TinkerCatalog.Leather, 1));
        var full = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 10), s, rows);
        Check.Eq(Seq(full), Seq(TinkerCatalog.YellowGarnet, TinkerCatalog.Leather), "capped item: no-slot rows, then Leather");

        var some = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 5), s, rows);
        Check.Eq(some[^1].Material, TinkerCatalog.Leather, "Irons fill the last 5 slots, then Leather");
        Check.Eq(Mats(some).Count(m => m == TinkerCatalog.Iron), 5, "five Irons");

        var open = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 5),
            Settings(("s" + TinkerCatalog.Leather, 1), ("r" + TinkerCatalog.Iron, 2)), rows);
        Check.True(!Mats(open).Contains(TinkerCatalog.Leather), "Leather waits for the 10 ordinary tinks");

        var retailOnly = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 10), s,
            TinkerPlanner.Rows(TinkerBench.Melee, false, null));
        Check.Eq(retailOnly.Count, 0, "off ILT: the server rows aren't offered");
    }

    private static void IltJewelry()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Jewelry, true, null);
        var s = Settings(("s" + TinkerCatalog.Serpentine, 8), ("s" + TinkerCatalog.Steel, 8),
                         ("s" + TinkerCatalog.Silver, 1), ("s" + TinkerCatalog.Copper, 1));
        var plan = TinkerPlanner.Plan(TinkerBench.Jewelry, Item(AcObjectClass.Jewelry, tinks: 0,
            flags: k => k == TinkerCatalog.IntJewelSilver ? 2 : 0), s, rows);
        var m = Mats(plan);
        Check.Eq(m.Count(x => x == TinkerCatalog.Serpentine), 8, "8 Serpentine");
        Check.Eq(m.Count(x => x == TinkerCatalog.Steel), 2, "Steel stops at 10 combined");
        Check.True(!m.Contains(TinkerCatalog.Silver), "Silver flag already 2");
        Check.Eq(m[0], TinkerCatalog.Copper, "flag rows first");
        Check.True(TinkerCatalog.IsTrinket("Ornate Compass") && !TinkerCatalog.IsTrinket("Yellow Topaz Ring"), "trinkets left to their recipes");
    }

    private static void ItemRows()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Items, false, null);
        var s = Settings(("r" + TinkerCatalog.Moonstone, 2), ("r" + TinkerCatalog.Leather, 1), ("r" + TinkerCatalog.Sandstone, 1));
        var noMana = TinkerPlanner.Plan(TinkerBench.Items, Item(AcObjectClass.Armor), s, rows);
        Check.Eq(Seq(noMana), Seq(TinkerCatalog.Leather), "no mana: no Moonstone; not retained: Leather, not Sandstone");

        var mana = new TinkerItemFacts { Class = AcObjectClass.Armor, Workmanship = 5, MaxMana = 800, Retained = true, Tinks = 10 };
        var retained = TinkerPlanner.Plan(TinkerBench.Items, mana, s, rows);
        Check.Eq(Seq(retained), Seq(TinkerCatalog.Sandstone), "capped + retained: only Sandstone (no slot)");
    }

    private static void Filters()
    {
        var s = Settings();
        s.MaxTinks = 8;
        s.MinFreeTinks = 3;
        s.SkipImbued = true;
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 9), s), "9 tinks (max 8)", "max tinks");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 8), s), "fewer than 3 free tinks", "min free");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, imbued: true), s), "imbued", "skip imbued");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, work: 0), s), "no workmanship", "untinkerable");
        var worn = Item(AcObjectClass.Armor);
        worn.Equipped = true;
        Check.Eq(TinkerPlanner.Excluded(worn, s), "equipped", "worn items left alone");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 2), s), "", "eligible");
        Check.True(TinkerCatalog.Targets(TinkerBench.Caster, AcObjectClass.WandStaffOrb)
                   && !TinkerCatalog.Targets(TinkerBench.Items, AcObjectClass.Salvage), "bench targets");
    }

    private static void Bags()
    {
        var melee = TinkerPlanner.Rows(TinkerBench.Melee, true, null);
        var rend = new TinkerStep(melee.First(r => r.Id == TinkerCatalog.RendId), TinkerCatalog.BlackGarnet);
        var abyssal = new TinkerStep(melee.First(r => r.NameMatch == "Abyssal"), TinkerCatalog.BlackGarnet);
        var iron = new TinkerStep(melee.First(r => r.Id == TinkerCatalog.Iron), TinkerCatalog.Iron);

        Check.True(TinkerPlanner.BagMatches(iron, "Salvage (100)", TinkerCatalog.Iron, 100), "full Iron bag");
        Check.True(!TinkerPlanner.BagMatches(iron, "Salvage (60)", TinkerCatalog.Iron, 60), "partial bag refused");
        Check.True(TinkerPlanner.BagMatches(rend, "Salvage (100)", TinkerCatalog.BlackGarnet, 100), "Black Garnet rend bag");
        Check.True(!TinkerPlanner.BagMatches(rend, "Bag of Abyssal-Touched Gems", TinkerCatalog.BlackGarnet, 100), "Abyssal isn't a rend");
        Check.True(TinkerPlanner.BagMatches(abyssal, "Bag of Abyssal-Touched Gems", TinkerCatalog.BlackGarnet, 100), "Abyssal row by name");
        Check.True(TinkerPlanner.BagMatches(rend, "Foolproof Black Garnet", 0, 100), "Foolproof rend by name");
        Check.True(!TinkerPlanner.BagMatches(rend, "Foolproof Black Opal", 0, 100), "Black Opal isn't Black Garnet");
        Check.True(TinkerPlanner.IsFoolproof("Foolproof Imperial Topaz"), "foolproof name");
    }

    private static void BagChoice()
    {
        var bags = new List<TinkerBagChoice> { new(1, 4, 0.60), new(2, 7, 0.85), new(3, 9, 0.95) };
        Check.Eq(TinkerPlanner.SelectBag(bags, 0), 2, "no minimum: best chance");
        Check.Eq(TinkerPlanner.SelectBag(bags, 80), 1, "minimum 80: the cheapest bag that reaches it");
        Check.Eq(TinkerPlanner.SelectBag(bags, 99), -1, "nothing reaches 99");
        var server = new List<TinkerBagChoice> { new(1, 5, null), new(2, 8, null) };
        Check.Eq(TinkerPlanner.SelectBag(server, 50), 1, "unknown chance: best workmanship");
        var tie = new List<TinkerBagChoice> { new(1, 9, 1.0), new(2, 3, 1.0) };
        Check.Eq(TinkerPlanner.SelectBag(tie, 0), 1, "tie: keep the better bag");
    }

    private static void Section()
    {
        Check.True(IltSections.TryParse("tinker", out IltSection s) && s == IltSection.Crafting, "/ra hub open tinker");
        Check.True(IltSections.TryParse("crafting", out s) && s == IltSection.Crafting, "crafting id");
        Check.Eq(IltSections.TabId(IltSection.Crafting), "crafting", "tab id the engine menu sends");
        Check.True(IltSections.IsUniversal(IltSection.Crafting), "every server");
        Check.True(IltSections.Tabs.Contains(IltSection.Crafting), "on the tab bar");
        Check.True(IltSections.IsTab(IltSection.Crafting), "a Mini Remote tab, not a panel");
    }
}

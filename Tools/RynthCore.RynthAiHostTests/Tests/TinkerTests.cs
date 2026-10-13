using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Crafting tab's tinkering (2026-10-12): ACE's retail chance (RecipeManager.GetTinkerChance),
// the planner's caps and order (imbue once, ILT no-slot salvage, Steel + Serpentine 10, Leather
// after the cap, rend last), bag matching / choice, and the universal Crafting tab id.
// Phase 2: ILT trinkets (flags 384-387, tier-prepared bags, Ammunition) and the Rings
// inscription chain (UB-IT's ingredient names, queue order and scroll pairing).
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
        r.Add("tinker: ILT trinkets (flags, order, tier bags, Ammunition)", Trinkets);
        r.Add("rings: ingredient names, alternates and excludes", RingIngredients);
        r.Add("rings: craft queue runs Step 1, then 2, then 3", RingQueue);
        r.Add("rings: scroll glyph and UB-IT pairing", RingScrolls);
    }

    private static TinkerBenchSettings Settings(params (string Key, int Count)[] counts)
    {
        var s = new TinkerBenchSettings { MaxTinks = 10 };
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
        // Crafting is one of the Mini Remote's universal tabs (every server), after Quests and Pets.
        Check.Eq(MiniRemoteTabs.Parse("tinker"), MiniRemoteTabs.Crafting, "/ra hub open tinker");
        Check.Eq(MiniRemoteTabs.Parse("crafting"), MiniRemoteTabs.Crafting, "crafting id");
        Check.Eq(MiniRemoteTabs.Crafting, "crafting", "tab id the engine menu sends");
        Check.True(MiniRemoteTabs.Universal.Contains(MiniRemoteTabs.Crafting), "on the tab bar, every server");
        Check.Eq(MiniRemoteTabs.Label(MiniRemoteTabs.Crafting), "Crafting", "tab label");
    }

    private static void Trinkets()
    {
        Check.Eq(TinkerPlanner.Rows(TinkerBench.Trinkets, false, null).Count, 0, "off ILT: no trinket rows");
        var rows = TinkerPlanner.Rows(TinkerBench.Trinkets, true, null);
        var s = Settings(("s" + TinkerCatalog.Pyreal, 1), ("s" + TinkerCatalog.Diamond, 1), ("s" + TinkerCatalog.Ruby, 1),
                         ("s" + TinkerCatalog.GromnieHide, 1));
        var all = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry), s, rows);
        Check.Eq(Seq(all), Seq(TinkerCatalog.Diamond, TinkerCatalog.Ruby, TinkerCatalog.GromnieHide, TinkerCatalog.Pyreal),
            "UB-IT order: Diamond, Ruby, Gromnie Hide, Pyreal");
        var some = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry,
            flags: k => k is TinkerCatalog.IntTrinketDiamond or TinkerCatalog.IntTrinketGromnie ? 2 : 0), s, rows);
        Check.Eq(Seq(some), Seq(TinkerCatalog.Ruby, TinkerCatalog.Pyreal), "flags 384 / 386 already 2");
        var capped = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry, tinks: 10), s, rows);
        Check.Eq(capped.Count, 4, "trinket salvage doesn't use tink slots");

        var diamond = new TinkerStep(rows.First(r => r.Id == TinkerCatalog.Diamond), TinkerCatalog.Diamond);
        Check.True(TinkerPlanner.BagMatches(diamond, "Major Diamond Salvage", TinkerCatalog.Diamond, 100, 2), "Major bag at Major");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Minor Diamond Salvage", TinkerCatalog.Diamond, 100, 2), "Minor bag at Major");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Salvage (100)", TinkerCatalog.Diamond, 100, 0), "raw bag isn't prepared");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Major Ruby Salvage", TinkerCatalog.Ruby, 100, 2), "other material");
        Check.True(TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Salvage (100)", TinkerCatalog.Diamond, 100), "raw bag");
        Check.True(!TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Moderate Diamond Salvage", TinkerCatalog.Diamond, 100), "prepared isn't raw");
        Check.True(!TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Salvage (55)", TinkerCatalog.Diamond, 55), "partial raw bag");
        Check.True(TinkerPlanner.IsTierAmmunition("Moderate Ammunition", 1), "Moderate Ammunition");
        Check.True(!TinkerPlanner.IsTierAmmunition("Major Ammunition", 1), "wrong tier");
        Check.True(!TinkerPlanner.IsTierAmmunition("Moderate Diamond Salvage", 1), "not Ammunition");
        Check.Eq(TinkerPlanner.TierOf("Minor Pyreal Salvage"), 0, "Minor");
        Check.Eq(TinkerPlanner.TierOf("Salvage (100)"), -1, "no tier");

        Check.True(TinkerCatalog.IsTrinket("Spinning Top") && TinkerCatalog.IsTrinket("Copper Pocket Watch")
                   && !TinkerCatalog.IsTrinket("Imperial Topaz Necklace"), "trinket names (topaz isn't a top)");
        Check.True(TinkerCatalog.Targets(TinkerBench.Trinkets, AcObjectClass.Jewelry), "trinkets are jewelry");
    }

    private static void RingIngredients()
    {
        var step1 = RingChain.Recipes[0];
        Check.True(RingChain.Matches("Quill of Infliction", step1.Tool), "base quill");
        Check.True(RingChain.Matches("Infliction Quill", step1.Tool), "alternate name");
        Check.True(!RingChain.Matches("Enlightened Quill of Infliction", step1.Tool), "Enlightened excluded from Step 1");
        Check.True(RingChain.Matches("Empyrean Trophy", step1.Target) && RingChain.Matches("Royal Summons", step1.Target), "trophies");
        Check.True(!RingChain.Matches("Trophy Kit", step1.Target), "not every Trophy");

        var step2 = RingChain.Recipes[1];
        Check.True(RingChain.Matches("Enlightened Quill of Infliction", step2.Tool), "Step 2 quill");
        Check.True(!RingChain.Matches("Enlightened Infliction Quill of Partition", step2.Tool), "partition quill excluded from Step 2");
        Check.True(RingChain.Matches("Vial of Ink of Partition", step2.Target), "ink");

        var flame = RingChain.Recipes.First(r => r.Id == "ring2_glyph_flame");
        Check.True(RingChain.Matches("Enlightened Infliction Quill of Partition", flame.Tool), "Step 3 quill");
        Check.True(RingChain.Matches("Glyph of Flame", flame.Target) && !RingChain.Matches("Glyph of Frost", flame.Target), "glyph by full name");
        Check.True(!RingChain.Matches(null, flame.Target), "no name");
    }

    private static void RingQueue()
    {
        var runs = new Dictionary<string, int> { ["ring2_glyph_frost"] = 1, ["ring2_step2_ink"] = 2, ["ring2_step1_quill_trophy"] = 2, ["ring2_glyph_bludgeoning"] = 1 };
        var q = RingChain.Expand(id => runs.TryGetValue(id, out int n) ? n : 0);
        Check.Eq(string.Join(",", q.Select(r => r.Tier)), "1,1,2,2,3,3", "tiers in order");
        Check.Eq(q[4].Id, "ring2_glyph_bludgeoning", "glyphs in definition order");
        Check.Eq(RingChain.Expand(_ => 500).Count, RingChain.Recipes.Length * RingChain.MaxRuns, "runs capped at 99");
        Check.Eq(RingChain.Expand(_ => 0).Count, 0, "nothing queued");
    }

    private static void RingScrolls()
    {
        Check.Eq(RingChain.GlyphOf("Inscription of Halo of Frost")?.Id, "ring2_glyph_frost", "Halo of Frost");
        Check.Eq(RingChain.GlyphOf("Inscription of Cassius' Ring of Fire")?.Id, "ring2_glyph_flame", "Ring of Fire");
        Check.Eq(RingChain.GlyphOf("Inscription of Something"), null, "unknown scroll");
        Check.True(RingChain.IsInscriptionScroll("Inscription of Clouded Soul") && !RingChain.IsInscriptionScroll("Glyph of Nether"), "scroll marker");

        var scrolls = new[] { (30, "Inscription of Searing Disc"), (10, "Inscription of Tectonic Rifts"), (20, "Inscription of Mystery") };
        var pairs = RingChain.PairScrolls(scrolls, new[] { 900, 500, 700, 800 });
        // Recipe ids sort bludgeoning < corrosion, unknown last; targets by id.
        Check.Eq(string.Join(";", pairs.Select(p => $"{p.Scroll}>{p.Target}")), "10>500;30>700;20>800", "glyph order onto target id order");
        Check.Eq(RingChain.PairScrolls(scrolls, Array.Empty<int>()).Count, 0, "no targets");
    }
}

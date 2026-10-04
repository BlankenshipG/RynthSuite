using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Weapons (2026-09-30): each weapon's element from its properties (DamageType, the icon's
// element glow, a rending imbue, the name), the identify pacing, the Items list keeping the
// user's element, full names with the material, the element-and-weapon rule (the weakness
// picks the wand), listed weapons only, the swap tracker (one in flight, retry cap, no
// flip-flop), and the tester's Dragon's Isle case: an unlisted fire wand wielded, Blades cast.
internal static class WeaponTests
{
    private const uint Player = 0x50000C01;

    public static void Register(Runner r)
    {
        r.Add("weapon element: DamageType, then the icon, then a rending, then the name", ElementSources);
        r.Add("weapon element: read through the host (ints 45/18/179, float 152)", ElementFromHost);
        r.Add("weapon element: one identify at a time, paced, at most twice per weapon", IdentifyPacing);
        r.Add("weapon element: the Items list fills in and keeps the user's pick", ItemsListElements);
        r.Add("weapon names: material in front, element after, saved by id", WeaponNaming);
        r.Add("element choice: the rule's damage type drives the wand", ChoiceRule);
        r.Add("element choice: the weakness picks the wand, not the wand in hand", ChoiceWeakness);
        r.Add("element choice: no wand of the weakness, the next-weakest a listed wand has", ChoiceNextWeakest);
        r.Add("element choice: a tie goes to the wand in hand, not to Slash", ChoiceTie);
        r.Add("element choice: weakness unknown, the wand in hand and its element; else Slash", ChoiceUnknown);
        r.Add("element choice: a matching rending wins for its element", ChoiceRending);
        r.Add("element choice: a wand always casts its own element (rule, Damage tab, no match)", ChoiceWandCastsOwn);
        r.Add("element choice: elements the character can't cast are skipped", ChoiceCastable);
        r.Add("slayer: beats the weakness and the Monsters rule's damage type", SlayerBeatsWeaknessAndRule);
        r.Add("slayer: a weapon the player picked (for the monster or on the DEFAULT row) beats it", SlayerVsDamageTab);
        r.Add("slayer: other creature types, no bonus and other kinds are ignored", SlayerNonMatching);
        r.Add("slayer: the bigger bonus wins, then the element order, then the hand", SlayerOrder);
        r.Add("slayer: creature type unknown, the old choice", SlayerUnknownType);
        r.Add("slayer: the element follows the chosen weapon", SlayerElement);
        r.Add("slayer: read through the host (int 166, float 138, monster int 2), cached by wcid", SlayerFromHost);
        r.Add("weapon choice: an unlisted wand is never picked (Damage tab, learned, pack, name)", UnlistedNeverPicked);
        r.Add("weapon swap: one in flight, confirmed, retry cap, then what's in hand", SwapOneInFlight);
        r.Add("weapon swap: no flip-flop between two weapons", SwapNoFlipFlop);
        r.Add("weapon swap: a server refusal counts as a failed try", SwapRefused);
        r.Add("tester's case: Dragon's Isle, an unlisted fire wand and Blades", TesterDragonsIsle);
    }

    // ── Element detection ─────────────────────────────────────────────────────

    private static void ElementSources()
    {
        var d = WeaponElements.Detect(0x10, null, null, "Frost Wand");
        Check.Eq(d.Element, "Fire", "DamageType Fire beats a Frost name");
        Check.Eq(d.Source, "properties", "source");
        Check.Eq(WeaponElements.Detect(0x3, null, null, "Sword").Element, "Slash", "Slash/Pierce sword: Slash");
        Check.Eq(WeaponElements.Detect(0x400, null, null, null).Element, "Nether", "nether caster");
        Check.Eq(WeaponElements.Detect(0x40, null, null, null).Element, "Lightning", "electric caster");

        var icon = WeaponElements.Detect(null, 0x0200, 0x0080, "Fire Wand");
        Check.Eq(icon.Element, "Cold", "not identified: the icon's Frost glow beats a Fire Rending and the name");
        Check.Eq(icon.Source, "icon", "source icon");
        Check.Eq(icon.Rending, "Fire", "the rending is still reported");

        var rend = WeaponElements.Detect(0, 0x0200, 0, "Ivory Wand");
        Check.Eq(rend.Element, "Fire", "no DamageType, no glow: the Fire Rending");
        Check.Eq(rend.Source, "rending", "source rending");

        var ui = new (int Fx, string E)[]
        {
            (0x20, "Fire"), (0x40, "Lightning"), (0x80, "Cold"), (0x100, "Acid"), (0x200, "Bludgeon"),
            (0x400, "Slash"), (0x800, "Pierce"), (0x1000, "Nether"), (0x1, ""), (0x0, ""), (0x21, "Fire"),
        };
        foreach (var (fx, e) in ui) Check.Eq(WeaponElements.FromUiEffects(fx), e, $"UiEffects 0x{fx:X}");
        var imb = new (int Imbue, string E)[]
        {
            (0x8, "Slash"), (0x10, "Pierce"), (0x20, "Bludgeon"), (0x40, "Acid"), (0x80, "Cold"),
            (0x100, "Lightning"), (0x200, "Fire"), (0x4000, "Nether"), (0x4, ""), (0x1, ""),
        };
        foreach (var (i, e) in imb) Check.Eq(WeaponElements.FromImbuedEffect(i), e, $"ImbuedEffect 0x{i:X}");

        var names = new (string Name, string E)[]
        {
            ("Flaming Wand", "Fire"), ("Flame Staff", "Fire"), ("Fire Orb", "Fire"), ("Frost Baton", "Cold"),
            ("Ice Scepter", "Cold"), ("Acid Wand", "Acid"), ("Lightning Staff", "Lightning"),
            ("Electric Orb", "Lightning"), ("Staff of Blades", "Slash"), ("Blade Wand", "Slash"),
            ("Piercing Orb", "Pierce"), ("Bludgeoning Staff", "Bludgeon"), ("Nether Orb", "Nether"),
            ("Iceberg Wand", ""), ("Ivory Wand", ""), ("", ""),
        };
        foreach (var (n, e) in names) Check.Eq(WeaponElements.FromName(n), e, $"name '{n}'");
        Check.Eq(WeaponElements.Detect(null, null, null, "Ivory Wand").Unknown, true, "nothing known: unknown (no Slash default)");
        Check.Eq(WeaponElements.Normalize("Frost"), "Cold", "Frost = Cold");
        Check.Eq(WeaponElements.Normalize("Blade"), "Slash", "Blade = Slash");
        Check.Eq(WeaponElements.Normalize("Electric"), "Lightning", "Electric = Lightning");
    }

    private static void ElementFromHost()
    {
        FakeHost.Reset();
        const uint wand = 0x60000031, sword = 0x60000032;
        FakeHost.Ints[(wand, 45)] = 0x10;
        FakeHost.Doubles[(wand, 152)] = 1.12;
        FakeHost.Ints[(sword, 18)] = 0x0100;     // an Acid glow, before any identify
        FakeHost.Ints[(sword, 179)] = 0x0040;    // Acid Rending
        var host = FakeHost.Create();
        var w = WeaponElementTracker.ReadFrom(host, unchecked((int)wand), "Wand");
        Check.Eq(w.Element, "Fire", "caster DamageType 45");
        Check.Eq(w.Bonus, 1.12, "ElementalDamageMod 152");
        var s = WeaponElementTracker.ReadFrom(host, unchecked((int)sword), "Sword");
        Check.Eq(s.Element, "Acid", "icon element");
        Check.Eq(s.Source, "icon", "source icon");
        Check.Eq(s.Rending, "Acid", "rending read");
    }

    private static void IdentifyPacing()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        var host = FakeHost.Create();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        var tr = new WeaponElementTracker(host, () => t);
        int[] ids = { 0x60000041, 0x60000042, 0x60000043 };

        Check.Eq(tr.Pump(ids), ids[0], "first: the first weapon not identified");
        Check.Eq(tr.Pump(ids), 0, "one at a time: nothing while it's in flight");
        t = t.AddMilliseconds(2100);
        Check.Eq(tr.Pump(ids), 0, "past the gap but not answered and not timed out: still waiting");
        FakeHost.Appraised.Add(unchecked((uint)ids[0]));
        Check.Eq(tr.Pump(ids), ids[1], "answered: the next one");
        t = t.AddMilliseconds(500);
        FakeHost.Appraised.Add(unchecked((uint)ids[1]));
        Check.Eq(tr.Pump(ids), 0, "answered, but paced: no request inside the gap");
        t = t.AddMilliseconds(2000);
        Check.Eq(tr.Pump(ids), ids[2], "after the gap: the third");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), ids[2], "never answered: asked a second time after the timeout");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), 0, "at most twice per weapon");
        Check.Eq(FakeHost.IdRequests.Count, 4, "four identifies in all");
        Check.Eq(tr.RequestsSent, 4, "tracker count");
    }

    private static void ItemsListElements()
    {
        var rules = new List<ItemRule>
        {
            new() { Id = 1, Name = "Old Sword", Element = "Slash" },                         // legacy default
            new() { Id = 2, Name = "Old Wand", Element = "Fire" },                           // legacy, not the default
            new() { Id = 3, Name = "Set Wand", Element = "Cold", ElementSource = "set" },    // the user's
            new() { Id = 4, Name = "Flame Wand", Element = "Fire", ElementSource = "name" }, // name, then identified
            new() { Id = 5, Name = "Ivory Wand", Element = "", ElementSource = "" },         // nothing known yet
        };
        var known = new Dictionary<int, WeaponElements.Info>
        {
            [1] = new("Pierce", "properties", "", 1.0),
            [2] = new("Acid", "properties", "", 1.0),
            [3] = new("Fire", "properties", "", 1.0),
            [4] = new("Acid", "properties", "", 1.0),
        };
        int changed = WeaponList.RefreshElements(rules, (id, _) => known.TryGetValue(id, out var i) ? i : new("", "", "", 1.0));
        Check.Eq(rules[0].Element, "Pierce", "a legacy Slash default is replaced once properties are known");
        Check.Eq(rules[1].Element, "Fire", "a legacy non-Slash element is kept as the user's");
        Check.Eq(rules[1].ElementSource, "set", "and marked as the user's");
        Check.Eq(rules[2].Element, "Cold", "the user's pick beats the properties");
        Check.Eq(rules[3].Element, "Acid", "properties beat the name");
        Check.Eq(rules[4].Element, "", "still unknown");
        Check.Eq(changed, 3, "three entries changed");

        // The panel round trip: it shows full names and "Unknown", sends them back; saved by id.
        var shown = WeaponList.ForDisplay(rules, r => "Silver " + r.Name);
        Check.Eq(shown[4].Element, "Unknown", "an unknown element shows as Unknown");
        Check.Eq(shown[0].Name, "Silver Old Sword", "the panel gets the full name");
        shown[0].Element = "Bludgeon";                 // the user picks an element
        shown.RemoveAt(1);                             // and deletes one
        var merged = WeaponList.MergeEdited(rules, shown);
        Check.Eq(merged.Count, 4, "the deletion is kept");
        Check.Eq(merged[0].Name, "Old Sword", "the saved name stays (matched by id, not by the shown name)");
        Check.Eq(merged[0].Element, "Bludgeon", "the user's element");
        Check.Eq(merged[0].ElementSource, "set", "is the user's");
        Check.Eq(merged[3].Element, "", "Unknown comes back as unknown");
        Check.Eq(merged[3].ElementSource, "", "and keeps no source");
        Check.Eq(merged[2].ElementSource, "properties", "an untouched entry keeps its source");
    }

    private static void WeaponNaming()
    {
        Check.Eq(WeaponNames.Full("Wand", 63), "Silver Wand", "Silver (63)");
        Check.Eq(WeaponNames.Full("Staff", 73), "Ebony Staff", "Ebony (73)");
        Check.Eq(WeaponNames.Full("Orb", 15), "Black Garnet Orb", "Black Garnet (15)");
        Check.Eq(WeaponNames.Full("Scepter", 26), "Imperial Topaz Scepter", "Imperial Topaz (26)");
        Check.Eq(WeaponNames.Full("Silver Wand", 63), "Silver Wand", "already named with it");
        Check.Eq(WeaponNames.Full("Wand", 0), "Wand", "no material");
        Check.Eq(WeaponNames.Full("Wand", 999), "Wand", "unknown material");
        Check.Eq(WeaponNames.WithElement("Silver Wand", "Fire"), "Silver Wand (Fire)", "with the element");
        Check.Eq(WeaponNames.WithElement("Silver Wand", ""), "Silver Wand", "no element");
        Check.Eq(WeaponNames.Material(53), "Armoredillo Hide", "ACE spelling, split into words");

        FakeHost.Reset();
        const uint wand = 0x60000051;
        FakeHost.Names[wand] = "Wand";
        FakeHost.Ints[(wand, 131)] = 63;
        var host = FakeHost.Create();
        Check.Eq(WeaponNames.For(host, null, unchecked((int)wand), "?"), "Silver Wand", "read through the host");
        Check.Eq(WeaponNames.For(host, null, 0x60000052, "Old Name"), "Old Name", "a gone object keeps the saved name");
    }

    // ── Element choice (WeaponPlanner) ────────────────────────────────────────

    private static WeaponCandidate Wand(int id, string elem, bool inHand = false, string rending = "")
        => new(id, "Wand " + id, CombatMode.Magic, elem, rending, inHand);

    private static CreatureWeakness.Ranking Rank(params (string E, double M)[] order)
        => new() { Source = "test", Order = order.OrderByDescending(o => o.M).ToList() };

    private static bool All(string e) => true;

    private static void ChoiceRule()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true), Wand(2, "Cold") };
        var weak = Rank(("Fire", 1.5), ("Cold", 0.5));
        var p = WeaponPlanner.Choose(wands, "Cold", weak, All);
        Check.Eq(p.WeaponId, 2, "the rule says Cold: the Cold wand, whatever the weakness and the hand");
        Check.Eq(p.Element, "Cold", "casts Cold");
        var q = WeaponPlanner.Choose(wands, "Frost", weak, All);
        Check.Eq(q.WeaponId, 2, "Frost is Cold");
        var none = WeaponPlanner.Choose(wands, "Acid", weak, All);
        Check.Eq(none.WeaponId, 1, "no Acid wand listed: keep the one in hand");
        Check.Eq(none.Element, "Fire", "and the wand in hand casts its own element (Fire), not the rule's Acid");
    }

    private static void ChoiceWeakness()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Cold", inHand: true), Wand(2, "Fire"), Wand(3, "Acid") };
        var weak = Rank(("Fire", 1.4), ("Acid", 1.1), ("Cold", 0.6), ("Slash", 1.0));
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "weak to Fire: the Fire wand, though Cold is in hand");
        Check.Eq(p.Element, "Fire", "casts Fire");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
    }

    private static void ChoiceNextWeakest()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Cold"), Wand(2, "Acid") };
        var weak = Rank(("Fire", 1.4), ("Acid", 1.1), ("Cold", 1.0), ("Lightning", 0.2));
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "no Fire wand: Acid, the next-weakest a listed wand has");
        Check.Eq(p.Element, "Acid", "casts Acid");

        var generic = new List<WeaponCandidate> { Wand(7, "", inHand: true) };
        var g = WeaponPlanner.Choose(generic, "", weak, All);
        Check.Eq(g.WeaponId, 7, "no listed wand has any element: keep the one in hand");
        Check.Eq(g.Element, "Fire", "and cast the weakest element");
    }

    private static void ChoiceTie()
    {
        // Physical 1.0 and Fire 1.0: the table lists Slash first. With a fire wand in hand the
        // tie goes to Fire (no swap, and not Blades from a fire wand).
        var weak = Rank(("Slash", 1.0), ("Pierce", 1.0), ("Bludgeon", 1.0), ("Fire", 1.0), ("Cold", 0.5));
        var wands = new List<WeaponCandidate> { Wand(1, "Cold"), Wand(2, "Fire", inHand: true), Wand(3, "Slash") };
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "the tie goes to the wand in hand");
        Check.Eq(p.Element, "Fire", "casts Fire, not Slash");

        var notInHand = new List<WeaponCandidate> { Wand(1, "Cold", inHand: true), Wand(2, "Fire"), Wand(3, "Slash") };
        var q = WeaponPlanner.Choose(notInHand, "", weak, All);
        Check.Eq(q.WeaponId, 3, "nothing of the tie in hand: the table order (Slash first)");
        Check.Eq(WeaponPlanner.TieGroups(weak).Count, 2, "two tie groups");
    }

    private static void ChoiceUnknown()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Cold", inHand: true) };
        var p = WeaponPlanner.Choose(wands, "", null, All);
        Check.Eq(p.WeaponId, 2, "weakness unknown: keep the wand in hand");
        Check.Eq(p.Element, "Cold", "and cast its element");
        Check.Eq(p.Source, "in hand (weakness unknown)", "source");

        var generic = new List<WeaponCandidate> { Wand(5, "") };
        var g = WeaponPlanner.Choose(generic, "", null, All);
        Check.Eq(g.Element, "Slash", "nothing known at all: Slash");
        var noSlash = WeaponPlanner.Choose(generic, "", null, e => e != "Slash");
        Check.Eq(noSlash.Element, "Fire", "can't cast Slash: the next in the fallback order");
        var none = WeaponPlanner.Choose(new List<WeaponCandidate>(), "", null, All);
        Check.Eq(none.WeaponId, 0, "nothing listed: no weapon");
    }

    private static void ChoiceRending()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Fire", rending: "Fire") };
        Check.Eq(WeaponPlanner.Choose(wands, "Fire", null, All).WeaponId, 2, "rule Fire: the Fire Rending wand");
        var weak = Rank(("Fire", 1.3), ("Cold", 1.0));
        Check.Eq(WeaponPlanner.Choose(wands, "", weak, All).WeaponId, 2, "weak to Fire: the Fire Rending wand");

        var swords = new List<WeaponCandidate>
        {
            new(10, "Sword", CombatMode.Melee, "Slash", "", false),
            new(11, "Rending Sword", CombatMode.Melee, "Slash", "Slash", false),
        };
        Check.Eq(WeaponPlanner.Choose(swords, "", Rank(("Slash", 1.2), ("Pierce", 1.0)), All).WeaponId, 11,
            "melee: the Slash Rending sword");
    }

    private static void ChoiceWandCastsOwn()
    {
        var weak = Rank(("Slash", 1.3), ("Cold", 1.0));
        var fire = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true) };

        // Damage tab fixed wand + a Monsters rule of another element: the wand's element.
        var fixedWand = WeaponPlanner.Choose(fire, "Acid", weak, All, fixedWeaponId: 1);
        Check.Eq(fixedWand.WeaponId, 1, "the Damage tab's wand");
        Check.Eq(fixedWand.Element, "Fire", "casts the wand's Fire, not the rule's Acid");

        // Auto, no listed wand has any element of the weakness: the wand in hand casts its own.
        var noMatch = WeaponPlanner.Choose(fire, "", Rank(("Slash", 1.3), ("Cold", 1.1)), All);
        Check.Eq(noMatch.Element, "Fire", "weak to Slash/Cold, only a Fire wand: casts Fire (not Blades)");

        // A rended wand with no other element: its rending is its element.
        var rended = new List<WeaponCandidate> { new(5, "Wand", CombatMode.Magic, "Cold", "Cold", true) };
        Check.Eq(WeaponPlanner.Choose(rended, "Acid", weak, All).Element, "Cold", "a Cold-rended wand casts Cold");

        // The character can't cast the wand's element: fall back to the rule.
        Check.Eq(WeaponPlanner.Choose(fire, "Acid", weak, e => e != "Fire").Element, "Acid",
            "can't cast Fire: the rule's Acid");
    }

    private static void ChoiceCastable()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Cold") };
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var p = WeaponPlanner.Choose(wands, "", weak, e => e != "Fire");
        Check.Eq(p.WeaponId, 2, "can't cast Fire: the Cold wand");
        Check.Eq(p.Element, "Cold", "casts Cold");

        // Only a wand whose element can't be cast (a Nether wand without Void Magic): keep it,
        // but cast the weakest element the character can, not the wand's uncastable Nether.
        var nether = new List<WeaponCandidate> { Wand(4, "Nether", inHand: true) };
        var n = WeaponPlanner.Choose(nether, "", Rank(("Nether", 1.5), ("Fire", 1.2), ("Cold", 0.8)), e => e != "Nether");
        Check.Eq(n.WeaponId, 4, "uncastable wand: keep the one in hand");
        Check.Eq(n.Element, "Fire", "and cast the weakest castable element (Fire), not Nether");
    }

    // ── Slayers (WeaponPlanner step 2) ────────────────────────────────────────

    private const int Olthoi = 1, Undead = 14;

    private static WeaponCandidate Slayer(int id, int kind, string elem, int type, double bonus, bool inHand = false, string rending = "")
        => new(id, "Slayer " + id, kind, elem, rending, inHand, type, bonus);

    private static void SlayerBeatsWeaknessAndRule()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Lightning", 0.5));
        var list = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true), Slayer(2, CombatMode.Magic, "Lightning", Olthoi, 2.0) };

        var auto = WeaponPlanner.Choose(list, "", weak, All, creatureType: Olthoi);
        Check.Eq(auto.WeaponId, 2, "Auto: the Olthoi slayer, though the monster is weak to the Fire wand in hand");
        Check.Eq(auto.Element, "Lightning", "the slayer wand casts its own element");
        Check.Eq(auto.Source, "slayer (Olthoi x2)", "source names the type and the bonus");

        var rule = WeaponPlanner.Choose(list, "Fire", weak, All, creatureType: Olthoi);
        Check.Eq(rule.WeaponId, 2, "a Monsters rule of Fire: still the slayer");
        Check.True(rule.Source.StartsWith("slayer"), "source is the slayer");

        // A learned best and the weapon in hand lose to it too.
        var learned = WeaponPlanner.Choose(list, "", null, All, learnedBestId: 1, creatureType: Olthoi);
        Check.Eq(learned.WeaponId, 2, "weakness unknown, learned best and in hand: the slayer");
    }

    private static void SlayerVsDamageTab()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate> { Wand(1, "Fire"), Slayer(2, CombatMode.Magic, "Cold", Olthoi, 2.0) };

        var pick = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, creatureType: Olthoi);
        Check.Eq(pick.WeaponId, 1, "the Damage tab's weapon for this monster is the player's pick: it wins");
        Check.Eq(pick.Source, "Damage tab", "source");

        var def = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default",
            creatureType: Olthoi, fixedIsDefault: true);
        Check.Eq(def.WeaponId, 1, "the Damage tab DEFAULT row's weapon is a player pick too: it beats a matching slayer");

        var defNoSlayer = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default",
            creatureType: Undead, fixedIsDefault: true);
        Check.Eq(defNoSlayer.WeaponId, 1, "no matching slayer: the default still beats the weakness");
        Check.Eq(defNoSlayer.Source, "Damage tab default", "source");
    }

    private static void SlayerNonMatching()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate>
        {
            Wand(1, "Fire"),
            Slayer(2, CombatMode.Magic, "Cold", Undead, 2.0),      // another type
            Slayer(3, CombatMode.Magic, "Cold", Olthoi, 0),        // no bonus property (ACE needs both)
            Slayer(4, CombatMode.Magic, "Cold", Olthoi, 1.0),      // x1: no gain
            Slayer(5, CombatMode.Melee, "Slash", Olthoi, 3.0),     // not the main kind (magic)
        };
        var p = WeaponPlanner.Choose(list, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 1, "none of them slays this Olthoi for a mage: weak to Fire, the Fire wand");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
        Check.False(WeaponPlanner.Slays(list[1], Olthoi), "an Undead slayer doesn't slay an Olthoi");
        Check.True(WeaponPlanner.Slays(list[1], Undead), "it slays Undead");
        Check.False(WeaponPlanner.Slays(list[1], 0), "type 0 (unknown) never matches");
    }

    private static void SlayerOrder()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Acid", 0.6));
        var bigger = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Magic, "Fire", Olthoi, 1.5),
            Slayer(2, CombatMode.Magic, "Acid", Olthoi, 3.0),
        };
        var p = WeaponPlanner.Choose(bigger, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 2, "x3 beats x1.5, though Fire is the weakness");
        Check.Eq(p.Source, "slayer (Olthoi x3)", "source");

        var same = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Magic, "Acid", Olthoi, 2.0, inHand: true),
            Slayer(2, CombatMode.Magic, "Fire", Olthoi, 2.0),
            Slayer(3, CombatMode.Magic, "Cold", Olthoi, 2.0),
        };
        Check.Eq(WeaponPlanner.Choose(same, "", weak, All, creatureType: Olthoi).WeaponId, 2,
            "same bonus: the weakest element (Fire), not the Acid one in hand");
        Check.Eq(WeaponPlanner.Choose(same, "Cold", weak, All, creatureType: Olthoi).WeaponId, 3,
            "same bonus and a rule of Cold: the Cold one");
        Check.Eq(WeaponPlanner.Choose(same, "", weak, e => e != "Fire", creatureType: Olthoi).WeaponId, 3,
            "can't cast Fire: Cold, the next weakest");

        var hand = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Melee, "Slash", Olthoi, 2.0),
            Slayer(2, CombatMode.Melee, "Slash", Olthoi, 2.0, inHand: true),
            Slayer(3, CombatMode.Melee, "Slash", Olthoi, 2.0, rending: "Slash"),
        };
        Check.Eq(WeaponPlanner.Choose(hand, "", null, All, creatureType: Olthoi).WeaponId, 3,
            "same bonus and element: the matching rending");
        hand.RemoveAt(2);
        Check.Eq(WeaponPlanner.Choose(hand, "", null, All, creatureType: Olthoi).WeaponId, 2,
            "then the one in hand (no needless swap)");
    }

    private static void SlayerUnknownType()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate> { Wand(1, "Fire"), Slayer(2, CombatMode.Magic, "Cold", Olthoi, 2.0, inHand: true) };
        var p = WeaponPlanner.Choose(list, "", weak, All);
        Check.Eq(p.WeaponId, 1, "creature type unknown: the weakness choice (Fire wand)");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
        var r = WeaponPlanner.Choose(list, "Cold", weak, All, creatureType: 0);
        Check.Eq(r.Source, "Monsters rule Cold", "and the rule as before");
    }

    private static void SlayerElement()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Slash", 0.8));

        var sword = new List<WeaponCandidate> { new(1, "Sword", CombatMode.Melee, "Fire", "", true), Slayer(2, CombatMode.Melee, "Slash", Olthoi, 2.0) };
        var m = WeaponPlanner.Choose(sword, "Cold", weak, All, creatureType: Olthoi);
        Check.Eq(m.WeaponId, 2, "the Slash slayer sword");
        Check.Eq(m.Element, "Slash", "its own element (what it hits with), not the rule's Cold");

        var blank = new List<WeaponCandidate> { Slayer(3, CombatMode.Magic, "", Olthoi, 2.0) };
        Check.Eq(WeaponPlanner.Choose(blank, "", weak, All, creatureType: Olthoi).Element, "Fire",
            "a slayer wand with no element casts the weakest element");
        Check.Eq(WeaponPlanner.Choose(blank, "Cold", weak, All, creatureType: Olthoi).Element, "Cold",
            "or the rule's element");

        var nether = new List<WeaponCandidate> { Slayer(4, CombatMode.Magic, "Nether", Olthoi, 2.0) };
        var n = WeaponPlanner.Choose(nether, "", weak, e => e != "Nether", creatureType: Olthoi);
        Check.Eq(n.WeaponId, 4, "a Nether slayer wand without Void Magic still slays (any war spell from it)");
        Check.Eq(n.Element, "Fire", "and casts the weakest element the character can");
    }

    private static void SlayerFromHost()
    {
        // Olthoi weakness (creature_types.txt): Fire 0.65 beats Lightning 0.35, so without the
        // slayer the Fire wand wins; the listed Lightning wand slays Olthoi x2.
        var rig = new Rig(990001, "Olthoi Testling");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        rig.C.InHandOverride = () => unchecked((int)FireWand);

        var before = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)before.WeaponId), FireWand, "no slayer properties yet: the Fire wand (weakness)");

        FakeHost.Ints[(ListedWand, 166)] = Olthoi;
        FakeHost.Doubles[(ListedWand, 138)] = 2.0;
        var cands = rig.C.ListedCandidates();
        var lw = cands.First(c => c.Id == unchecked((int)ListedWand));
        Check.Eq(lw.SlayerType, Olthoi, "int 166 read");
        Check.Near(lw.SlayerBonus, 2.0, 1e-9, "float 138 read");

        var noType = new WorldObject(unchecked((int)Monster) + 1, "Olthoi Testling", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 1] = 990001;
        Check.Eq(unchecked((uint)rig.C.PlanFor(noType, null).WeaponId), FireWand,
            "the monster isn't appraised (no int 2): no guess from the name, the weakness choice");

        FakeHost.Ints[(Monster, 2)] = Olthoi;
        var target = new WorldObject(unchecked((int)Monster), "Olthoi Testling", AcObjectClass.Monster);
        var p = rig.C.PlanFor(target, null);
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "appraised Olthoi: the slayer wand");
        Check.Eq(p.Source, "slayer (Olthoi x2)", "source");

        // The next spawn of the same wcid: known at once, before its own appraisal.
        var next = new WorldObject(unchecked((int)Monster) + 2, "Olthoi Testling", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 2] = 990001;
        Check.Eq(unchecked((uint)rig.C.PlanFor(next, null).WeaponId), ListedWand, "the next spawn: cached by wcid");
        Check.Eq(rig.C.CreatureTypeOf(990001, "Olthoi Testling"), Olthoi, "cache");
    }

    // ── Listed weapons only ───────────────────────────────────────────────────

    private const uint ListedWand = 0x60000061, FireWand = 0x60000062, NamedJunk = 0x60000063, Monster = 0x80000201;

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public WorldObject Target;

        public Rig(uint wcid, string monsterName)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[ListedWand] = "Wand"; FakeHost.ItemTypes[ListedWand] = 0x8000;
            FakeHost.Ints[(ListedWand, 45)] = 0x40;            // identified: Lightning
            FakeHost.Names[FireWand] = "Fire Wand"; FakeHost.ItemTypes[FireWand] = 0x8000;
            FakeHost.Ints[(FireWand, 18)] = 0x20;              // Fire glow
            FakeHost.Names[NamedJunk] = "Fire Wand"; FakeHost.ItemTypes[NamedJunk] = 0x8;   // jewelry named like a wand
            FakeHost.Wcids[Monster] = wcid;
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { ListedWand, FireWand, NamedJunk });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.CanCastOverride = e => e != "Nether";            // a War mage without Void
            Target = new WorldObject(unchecked((int)Monster), monsterName, AcObjectClass.Monster);
            S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)ListedWand), Name = "Wand", Element = "Lightning", ElementSource = "properties" } };
        }
    }

    private static void UnlistedNeverPicked()
    {
        var rig = new Rig(1535, "Ethereal Wisp");
        rig.Store.SetDefaultWeapon(FireWand);                  // picked on the Damage tab before the list edit
        rig.Store.SetManualWeapon(1535, FireWand);
        for (int i = 0; i < 4; i++) rig.Store.RecordHit(FireWand, 1535, "Ethereal Wisp", "Slash", 7, 300, false);
        Check.Eq(rig.Store.GetBestWeapon(1535), FireWand, "the Damage tab learned the fire wand as best");
        rig.C.InHandOverride = () => unchecked((int)FireWand);

        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the listed wand, not the Damage tab's unlisted fire wand");
        Check.True(FakeHost.Logs.Any(l => l.Contains("isn't in the Items list")), "the skipped pick is logged");

        // The wand lookups (buffing, recalls, debuffs) take listed wands only.
        var cache = FakeHost.MakeCache(FakeHost.Create(), Player, new[] { ListedWand, FireWand, NamedJunk });
        WorldObject? Look(int id) => cache[id];
        var inv = new[] { cache[unchecked((int)FireWand)]!, cache[unchecked((int)NamedJunk)]! };
        int got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, inv, allowUnlisted: true, out bool unlisted);
        Check.Eq(unchecked((uint)got), ListedWand, "a wand is listed: that one, never the pack's");
        Check.False(unlisted, "not from the pack");

        var none = new List<ItemRule>();
        got = WeaponList.FindWand(none, Look, _ => false, new[] { cache[unchecked((int)NamedJunk)]! }, allowUnlisted: true, out _);
        Check.Eq(got, 0, "no wand listed: a jewel named 'Fire Wand' is never taken (no name match)");
        got = WeaponList.FindWand(none, Look, _ => false, inv, allowUnlisted: false, out _);
        Check.Eq(got, 0, "setting off: nothing from the pack");
        got = WeaponList.FindWand(none, Look, _ => false, inv, allowUnlisted: true, out unlisted);
        Check.Eq(unchecked((uint)got), FireWand, "setting on and no wand listed: a real caster from the pack");
        Check.True(unlisted, "flagged as unlisted");

        var staff = new WorldObject(0x60000064, "Quarter Staff", AcObjectClass.MeleeWeapon);
        Check.False(WeaponList.IsWand(staff), "a melee Quarter Staff is not a wand");
        Check.True(WeaponList.IsWand(new WorldObject(0x60000065, "Ivory Staff", AcObjectClass.Unknown)), "an unclassified 'Staff' still is");
    }

    // ── Swap tracker ──────────────────────────────────────────────────────────

    private static void SwapOneInFlight()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        Check.Eq(tr.Next(2, 1, t), WeaponSwapTracker.Step.Swap, "B wanted, A in hand: swap");
        tr.Sent(2, 1, t);
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(500)), WeaponSwapTracker.Step.Wait, "one in flight: wait");
        Check.Eq(tr.Next(3, 1, t.AddMilliseconds(600)), WeaponSwapTracker.Step.Wait, "even for another weapon");
        Check.Eq(tr.Next(2, 2, t.AddMilliseconds(900)), WeaponSwapTracker.Step.InHand, "B seen in hand: confirmed");
        Check.Eq(tr.PendingId, 0, "nothing in flight");

        // A weapon that never arrives: 3 tries, then rest and fight with what's in hand.
        var tr2 = new WeaponSwapTracker();
        for (int i = 1; i <= 3; i++)
        {
            Check.Eq(tr2.Next(5, 1, t), WeaponSwapTracker.Step.Swap, $"try {i}: swap");
            tr2.Sent(5, 1, t);
            t = t.AddMilliseconds(2600);
        }
        Check.Eq(tr2.Next(5, 1, t), WeaponSwapTracker.Step.Hold, "after 3 tries: hold, fight with what's in hand");
        Check.True(tr2.Notice != null && tr2.Notice.Contains("didn't wield after 3 tries"), "logged once");
        tr2.Notice = null;
        Check.Eq(tr2.Next(5, 1, t.AddSeconds(30)), WeaponSwapTracker.Step.Hold, "still resting 30 s later");
        Check.True(tr2.Notice == null, "not logged again");
        Check.Eq(tr2.Next(5, 1, t.AddMinutes(2.1)), WeaponSwapTracker.Step.Swap, "after the rest: try again");
    }

    private static void SwapNoFlipFlop()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        tr.Next(2, 1, t); tr.Sent(2, 1, t);
        tr.Next(2, 2, t.AddSeconds(1));                                           // A -> B landed
        Check.Eq(tr.Next(1, 2, t.AddSeconds(3)), WeaponSwapTracker.Step.Hold, "straight back to A: held");
        Check.Eq(tr.Next(1, 2, t.AddSeconds(19)), WeaponSwapTracker.Step.Hold, "still held inside 20 s");
        Check.Eq(tr.Next(1, 2, t.AddSeconds(22)), WeaponSwapTracker.Step.Swap, "after 20 s it may go back");

        // Many swaps in a minute: held.
        var tr2 = new WeaponSwapTracker();
        int hand = 1;
        for (int i = 0; i < 4; i++)
        {
            int want = 10 + i;
            t = t.AddSeconds(5);
            tr2.Next(want, hand, t); tr2.Sent(want, hand, t);
            tr2.Next(want, want, t.AddSeconds(1));
            hand = want;
        }
        Check.Eq(tr2.Next(99, hand, t.AddSeconds(5)), WeaponSwapTracker.Step.Hold, "a fifth swap inside a minute: held");
    }

    private static void SwapRefused()
    {
        var tr = new WeaponSwapTracker { MaxTries = 2 };
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        tr.Next(2, 1, t); tr.Sent(2, 1, t);
        tr.Refused(t.AddMilliseconds(200));
        Check.Eq(tr.PendingId, 0, "a refusal ends the swap in flight");
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(300)), WeaponSwapTracker.Step.Swap, "retry without waiting out the timeout");
        tr.Sent(2, 1, t.AddMilliseconds(300));
        tr.Refused(t.AddMilliseconds(400));
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(500)), WeaponSwapTracker.Step.Hold, "second refusal with MaxTries 2: rest");
    }

    // ── The tester's case ─────────────────────────────────────────────────────

    /// <summary>
    /// Dragon's Isle (2026-09-30): the tester cut the Items list down to the wands used there,
    /// and the bot went in with another fire wand and cast Blades. Reproduced with a monster
    /// whose weakness table ties Slash, Pierce, Bludgeon and Nether at the top (Ethereal Wisp:
    /// Lightning 0.8, Fire 0.55, Acid 0.4, Cold 0), the fire wand in hand, and the Damage tab
    /// (default weapon and learned best) still naming the fire wand from before the list edit.
    /// Before the fix: the Damage tab pick won without checking the list, Magic mode trusted
    /// whatever wand was in hand, and Auto took Slash (first of the tie) whatever the wand.
    /// </summary>
    private static void TesterDragonsIsle()
    {
        var weak = CreatureWeakness.Rank(1535, "Ethereal Wisp", 0, null);
        Check.True(weak != null && weak.Source == "server data", "the table has the Ethereal Wisp");
        if (weak == null) return;
        Check.Eq(weak.Order[0].Element, "Slash", "the table lists Slash first (the old Auto pick)");

        var rig = new Rig(1535, "Ethereal Wisp");
        rig.Store.SetDefaultWeapon(FireWand);
        int inHand = unchecked((int)FireWand);
        rig.C.InHandOverride = () => inHand;

        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the listed (Lightning) wand, not the fire wand");
        Check.Eq(plan.Element, "Lightning", "Lightning: the weakest element a listed wand has (Slash/Pierce/Bludgeon/Nether have none)");
        Check.True(plan.Element != "Slash", "no Blades");

        // Magic mode with the fire wand in hand: combat swaps to the listed wand, once.
        var equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool ready = (bool)equip.Invoke(rig.C, new object[] { rig.Target, "Auto" })!;
        Check.False(ready, "not ready: a swap was sent");
        Check.Eq(FakeHost.Uses.Count, 1, "one UseObject");
        Check.Eq(FakeHost.Uses.FirstOrDefault(), ListedWand, "for the listed wand");
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        Check.Eq(FakeHost.Uses.Count, 1, "one swap in flight: nothing more while it lands");
        Check.False(FakeHost.Uses.Contains(FireWand), "the fire wand is never used");

        inHand = unchecked((int)ListedWand);                 // it landed
        ready = (bool)equip.Invoke(rig.C, new object[] { rig.Target, "Auto" })!;
        Check.True(ready, "listed wand in hand, Magic mode: ready to cast");
        Check.Eq(FakeHost.Uses.Count, 1, "no further swaps");

        // With only the fire wand listed (and in hand), it casts Fire, the weakest element it has.
        rig.S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" } };
        inHand = unchecked((int)FireWand);
        var fire = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)fire.WeaponId), FireWand, "the fire wand, now listed");
        Check.Eq(fire.Element, "Fire", "casts Fire from it, not Blades");
    }
}

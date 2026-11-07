using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// "Add to loot profile"'s attribute checklist (2026-10-18): every attribute a clicked item has,
// grouped, each ticked or not with a comparison and a value; spells by tier; pet essences.
// The defaults (no choices) are covered by LootAddItemTests / LootAddT11Tests, which still
// expect the rules "items like this" and "Include T11 attributes" always made.
internal static partial class Program
{
    private static void RunLootAddAttributeTests()
    {
        Console.WriteLine("\n-- LootAdd: attribute checklist --");
        TestAttrRowsAndGroups();
        TestAttrChoices();
        TestAttrSpellTiers();
        TestAttrCantripCounts();
        TestAttrPetEssence();
        TestAttrNativeAndWire();
        TestAttrVocab();
        TestAttrImbues();
        TestBlankRule();
    }

    private static LootItemFacts Sword() => new()
    {
        Id = 10, Name = "Gold Tachi", ObjectClass = (int)AcObjectClass.MeleeWeapon,
        Ints = { [131] = 60, [105] = 8, [159] = 45, [44] = 52, [5] = 300, [370] = 3, [374] = 2, [160] = 400, [158] = 2 },
        Doubles = { [62] = 1.18, [29] = 1.2 },
        Spells = { "Legendary Blood Thirst", "Epic Heart Thirst", "Major Strength", "Blood Drinker Self VI" },
    };

    private static List<LootEditItemChoice> Pick(params (string Id, bool On, LootItemAttributeOp Op, string Value)[] p)
        => p.Select(x => new LootEditItemChoice { Id = x.Id, On = x.On, Op = (int)x.Op, Value = x.Value }).ToList();

    private static void TestAttrRowsAndGroups()
    {
        LootItemRuleDraft d = BuildOk(Sword());
        List<LootItemAttribute> rows = d.Attributes;
        Check(rows.Count > 0, "attr: rows for an item");
        Check(!rows.Any(r => r.On), "attr: name + class ticks nothing by default");
        string[] groups = rows.Select(r => r.Group).Distinct().ToArray();
        Eq(string.Join(",", groups), "Item,Weapon / Armor,Ratings,Spells,Requirements", "attr: groups in display order");
        LootItemAttribute mat = rows.Single(r => r.Id == "L:131");
        Eq(mat.ItemText, "Gold", "attr: named value shown by name");
        Check(!mat.Editable && mat.Ops[0] == LootItemAttributeOp.Exactly, "attr: material is = (or !=), not typed");
        LootItemAttribute burden = rows.Single(r => r.Id == "L:5");
        Eq(burden.Ops[0], LootItemAttributeOp.AtMost, "attr: burden defaults to at most");
        Check(rows.Any(r => r.Id == "ratings" && r.ItemText == "5"), "attr: total ratings row (3 + 2)");
        Check(rows.Any(r => r.Id == "D:62") && rows.Any(r => r.Id == "D:29"), "attr: weapon modifiers (doubles)");
        Check(!rows.Any(r => r.Group == LootItemAttributes.GroupPet), "attr: no pet rows on a sword");

        // "Items like this" ticks what it always required, nothing more.
        LootItemRuleDraft like = BuildOk(Sword(), new() { Match = LootItemMatch.Like });
        Eq(string.Join(",", like.Attributes.Where(r => r.On).Select(r => r.Id)),
            "L:131,L:105,L:159,L:44,ratings,spell:Legendary Blood Thirst,spell:Epic Heart Thirst,spell:Major Strength,spell:Blood Drinker Self VI",
            "attr: like ticks material, workmanship, wield skill, damage, ratings, the first 4 spells");
    }

    private static void TestAttrChoices()
    {
        // Burden at most 250 (typed), attack modifier exactly (two double conditions), material !=,
        // and a row the item doesn't have (ignored).
        List<LootEditItemChoice> c = Pick(
            ("L:5", true, LootItemAttributeOp.AtMost, "250"),
            ("D:62", true, LootItemAttributeOp.Exactly, ""),
            ("L:131", true, LootItemAttributeOp.Not, ""),
            ("L:9999", true, LootItemAttributeOp.AtLeast, "1"));
        LootItemRuleDraft d = BuildOk(Sword(), new() { Choices = c });
        string conds = string.Join(" ; ", d.Rule.Conditions.Select(Lines));
        Eq(conds, "7:1 ; 1:^Gold Tachi$|1 ; 13:60|131 ; 2:250|5 ; 5:1.18|62 ; 4:1.18|62",
            "attr: name + class, then the ticked rows in row order with their comparisons");
        Eq(d.Attributes.Single(r => r.Id == "L:5").ToWire().Value, "250", "attr: the typed value goes back to the popup");
        Check(d.Notes.Count == 0, "attr: no notes");

        // A bad typed value keeps the item's and says so; an op the row doesn't offer is ignored.
        LootItemRuleDraft bad = BuildOk(Sword(), new() { Choices = Pick(("L:105", true, LootItemAttributeOp.SpellOrBetter, "lots")) });
        Check(bad.Rule.Conditions.Any(x => Lines(x) == "3:8|105"), "attr: bad value: the item's 8, at least");
        Check(bad.Notes.Any(n => n.StartsWith("Workmanship: 'lots'")), "attr: bad value: a note");
        LootItemRuleDraft frac = BuildOk(Sword(), new() { Choices = Pick(("L:105", true, LootItemAttributeOp.AtLeast, "7.5")) });
        Check(frac.Notes.Any(n => n.Contains("whole number")), "attr: a long row takes whole numbers only");

        // "Items like this" with a row unticked: the choice wins over the default.
        LootItemRuleDraft like = BuildOk(Sword(), new() { Match = LootItemMatch.Like, Choices = Pick(("L:131", false, LootItemAttributeOp.Exactly, "")) });
        Check(!like.Rule.Conditions.Any(x => Lines(x) == "12:60|131"), "attr: like, material unticked");
        Check(like.Rule.Conditions.Any(x => Lines(x) == "3:8|105"), "attr: like, the other defaults stay");
        Check(!like.Notes.Any(n => n.Contains("more than 4 spells")), "attr: no 4-spell note once the player chooses");
    }

    private static void TestAttrSpellTiers()
    {
        Eq(LootItemAttributes.SpellPattern("Major Strength", LootItemAttributeOp.SpellOrBetter), "^(Major|Epic|Legendary|Paragon(?:'s)?) Strength$", "spell: cantrip or better");
        Eq(LootItemAttributes.SpellPattern("Major Strength", LootItemAttributeOp.SpellAnyTier), "^(Minor|Major|Epic|Legendary|Paragon(?:'s)?) Strength$", "spell: cantrip any tier");
        Eq(LootItemAttributes.SpellPattern("Major Strength", LootItemAttributeOp.SpellExact), "^Major Strength$", "spell: exact");
        Eq(LootItemAttributes.SpellPattern("Blood Drinker Self VI", LootItemAttributeOp.SpellOrBetter), "^Blood Drinker Self (VI|VII|VIII)$", "spell: level or better");
        Eq(LootItemAttributes.SpellPattern("Legendary Blood Thirst", LootItemAttributeOp.SpellOrBetter), "^(Legendary|Paragon(?:'s)?) Blood Thirst$", "spell: Legendary or better is Legendary");
        Eq(LootItemAttributes.SpellPattern("Aura of Defender", LootItemAttributeOp.SpellOrBetter), "^Aura of Defender$", "spell: no tier: exact");
        Eq(LootItemAttributes.SpellPattern("Major Strength (II)", LootItemAttributeOp.SpellOrBetter), "^(Major|Epic|Legendary|Paragon(?:'s)?) Strength \\(II\\)$", "spell: the rest is escaped");

        string orBetter = LootItemAttributes.SpellPattern("Epic Heart Thirst", LootItemAttributeOp.SpellOrBetter);
        Check(Regex.IsMatch("Legendary Heart Thirst", orBetter, RegexOptions.IgnoreCase), "spell: Legendary is better than Epic");
        Check(!Regex.IsMatch("Major Heart Thirst", orBetter, RegexOptions.IgnoreCase), "spell: Major is not");
        Check(!Regex.IsMatch("Epic Heart Thirsty", orBetter, RegexOptions.IgnoreCase), "spell: still the whole name");

        List<LootItemAttribute> rows = BuildOk(Sword()).Attributes;
        Eq(rows.Single(r => r.Id == "spell:Major Strength").Ops[0], LootItemAttributeOp.SpellOrBetter, "spell: a cantrip defaults to or better");
        Eq(rows.Single(r => r.Id == "spell:Blood Drinker Self VI").Ops[0], LootItemAttributeOp.SpellExact, "spell: a leveled spell defaults to exact (as before)");
        Eq(rows.Single(r => r.Id == "spell:Major Strength").ItemText, "Major cantrip", "spell: the tier is shown");

        // Like with every spell ticked: no limit of four once chosen.
        var many = new LootItemFacts { Name = "Robe", ObjectClass = (int)AcObjectClass.Clothing, Spells = { "Minor Focus", "Major Willpower", "Epic Strength", "Legendary Endurance", "Major Coordination" } };
        LootItemRuleDraft d = BuildOk(many, new() { Match = LootItemMatch.Like, Choices = Pick(("spell:Major Coordination", true, LootItemAttributeOp.SpellAnyTier, "")) });
        Eq(d.Rule.Conditions.Count(x => x.NodeType == VTankNodeTypes.SpellNameMatch), 5, "spell: a fifth spell can be ticked");
        Check(d.Rule.Conditions.Any(x => Lines(x) == "0:^(Minor|Major|Epic|Legendary|Paragon(?:'s)?) Coordination$"), "spell: any tier pattern in the rule");
    }

    private static void TestAttrCantripCounts()
    {
        List<LootItemAttribute> rows = BuildOk(Sword()).Attributes;
        Eq(string.Join(",", rows.Where(r => r.Id.StartsWith("cantrips:")).Select(r => r.Label + "=" + r.ItemText)),
            "Legendary-or-better cantrips=1,Epic-or-better cantrips=2,Major-or-better cantrips=3", "cantrips: a row per tier the item has, highest first");
        Check(rows.Any(r => r.Id == "spellcount" && r.ItemText == "4"), "cantrips: spell count");

        LootItemRuleDraft d = BuildOk(Sword(), new() { Choices = Pick(("cantrips:Epic", true, LootItemAttributeOp.AtLeast, "2"), ("spellcount", true, LootItemAttributeOp.AtLeast, "3")) });
        Check(d.Rule.Conditions.Any(x => Lines(x) == "9:^(Epic|Legendary|Paragon(?:'s)?) ||2"), "cantrips: Spell Match, Epic or Legendary, at least 2");
        Check(d.Rule.Conditions.Any(x => Lines(x) == "8:3"), "cantrips: spell count >= 3");
        Check(LootItemRules.PreviewLines(d.Rule).Any(l => l.Contains(">= 2 spells matching")), "cantrips: preview in words");
    }

    private static void TestAttrPetEssence()
    {
        var wolf = new LootItemFacts
        {
            Id = 20, Name = "Fire Wolf Essence", ObjectClass = (int)AcObjectClass.Misc,
            Ints = { [9053] = 12, [369] = 180, [9056] = 40, [362] = 1, [9075] = 2, [9070] = 1, [9054] = 16, [370] = 5, [92] = 30, [91] = 50 },
        };
        List<LootItemAttribute> rows = BuildOk(wolf).Attributes;
        Check(rows.Any(r => r.Group == LootItemAttributes.GroupPet), "pet: bond makes it an essence");
        Eq(rows.Single(r => r.Id == "L:362").ItemText, "Primalist", "pet: mastery by name");
        Eq(rows.Single(r => r.Id == "L:9054").ItemText, "Fire", "pet: captured damage type by name");
        Check(rows.Any(r => r.Id == "L:92" && r.Label == "Uses left"), "pet: uses (Item group)");

        LootItemRuleDraft like = BuildOk(wolf, new() { Match = LootItemMatch.Like });
        string conds = string.Join(" ; ", like.Rule.Conditions.Select(Lines));
        Eq(conds, "7:8 ; 2007:5 ; 3:12|9053 ; 3:180|369 ; 3:40|9056 ; 12:1|362 ; 3:2|9075",
            "pet: like = class, ratings, bond, level, potency, mastery, mutations");

        var flagged = new LootItemFacts { Name = "Odd Essence", ObjectClass = (int)AcObjectClass.Misc, IsPetEssence = true, Ints = { [9073] = 1 } };
        Check(BuildOk(flagged).Attributes.Any(r => r.Id == "L:9073"), "pet: the plugin's IsPetEssence shows the group");
        var notPet = new LootItemFacts { Name = "Jibril's Essence", ObjectClass = (int)AcObjectClass.Misc, Ints = { [9073] = 1 } };
        Check(!BuildOk(notPet).Attributes.Any(r => r.Group == LootItemAttributes.GroupPet), "pet: a name alone isn't an essence");
    }

    private static void TestAttrNativeAndWire()
    {
        LootItemRuleDraft d = BuildOk(Sword(), new() { Choices = Pick(("L:5", true, LootItemAttributeOp.AtMost, ""), ("spell:Major Strength", true, LootItemAttributeOp.SpellOrBetter, ""), ("cantrips:Legendary", true, LootItemAttributeOp.AtLeast, "")) });
        var dropped = new List<string>();
        LootRule? n = LootItemRules.ToNative(d.Rule, dropped);
        Check(n != null && n.Conditions.Any(c => c.ToString().Contains("LongValKey") || c.ToString().Contains("[5]")), "native: burden converts");
        Eq(dropped.Count, 2, "native: the two spell conditions are named as left out");

        var st = new LootEditState { ItemDraft = new LootEditItemDraft { Seq = 4, AttributesSupported = true } };
        foreach (LootItemAttribute r in d.Attributes) st.ItemDraft.Attributes.Add(r.ToWire());
        string json = System.Text.Json.JsonSerializer.Serialize(st, LootEditJsonContext.Default.LootEditState);
        LootEditState? back = System.Text.Json.JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditState);
        LootEditItemAttribute? spell = back?.ItemDraft?.Attributes.FirstOrDefault(a => a.Id == "spell:Major Strength");
        Check(back?.ItemDraft?.AttributesSupported == true, "wire: attributes supported round-trips");
        Check(spell != null && spell.On && spell.Op == (int)LootItemAttributeOp.SpellOrBetter && spell.Ops.Count == 3, "wire: a row round-trips");

        var cmd = new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { Choices = Pick(("L:5", true, LootItemAttributeOp.AtMost, "9")) } };
        string cj = System.Text.Json.JsonSerializer.Serialize(cmd, LootEditJsonContext.Default.LootEditCommand);
        LootEditCommand? cb = System.Text.Json.JsonSerializer.Deserialize(cj, LootEditJsonContext.Default.LootEditCommand);
        Eq(cb?.Item?.Choices?.Single().Value, "9", "wire: choices round-trip");
        LootEditCommand? old = System.Text.Json.JsonSerializer.Deserialize("{\"Op\":\"item_preview\",\"Item\":{\"Match\":2}}", LootEditJsonContext.Default.LootEditCommand);
        Check(old?.Item != null && old.Item.Choices == null, "wire: an older engine's ask has no choices (defaults)");
    }

    private static void TestAttrVocab()
    {
        Eq(LootRuleText.ValueName(179, "4"), "Armor Rending", "vocab: imbue names (ImbuedEffect is 179)");
        Eq(LootRuleText.ValueName(353, "9"), "Crossbow", "vocab: 353 is the weapon type, not the imbue");
        Eq(LootRuleText.ValueName(353, "4"), "Mace", "vocab: 353 = 4 is a mace");
        Check(LootRuleText.ValueTableIsFlags(179) && !LootRuleText.ValueTableIsFlags(353), "imbue is flags, weapon type isn't");
        Eq(LootRuleText.ValueName(362, "3"), "Naturalist", "vocab: summoning mastery");
        Check(LootRuleText.LongKeys.Any(k => k.Id == 9053) && LootRuleText.LongKeys.Any(k => k.Id == 9075), "vocab: pet keys named");
        Check(LootRuleText.LongKeys.Any(k => k.Id == 92 && k.Name.StartsWith("UsesRemaining")), "vocab: 92 is uses remaining");
        Check(LootRuleText.LongKeys.Any(k => k.Id == 107 && k.Name == "ItemCurMana") && LootRuleText.LongKeys.Any(k => k.Id == 108 && k.Name == "ItemMaxMana"), "vocab: current and max mana");
        var ids = LootRuleText.LongKeys.Select(k => k.Id).ToList();
        Eq(ids.Count, ids.Distinct().Count(), "vocab: no long key named twice");
    }

    // 2026-11-07: imbues are 179 (with 303-306), "has any" picks several, and Add rule's blank checklist.
    private static void TestAttrImbues()
    {
        var wand = new LootItemFacts
        {
            Id = 20, Name = "Fire Wand", ObjectClass = (int)AcObjectClass.WandStaffOrb,
            Ints = { [179] = 4, [303] = 512, [353] = 12 },
        };
        Eq(wand.Int(179), 516, "imbues: 179 is ORed with ImbuedEffect2-5");
        LootItemRuleDraft? d = LootItemRules.Build(wand, new LootItemRuleOptions { Match = LootItemMatch.Like }, out string err);
        LootItemAttribute? imbue = d?.Attributes.FirstOrDefault(a => a.Id == "L:179");
        LootItemAttribute? wtype = d?.Attributes.FirstOrDefault(a => a.Id == "L:353");
        Check(imbue != null && imbue.Ops[0] == LootItemAttributeOp.HasAny, "imbues: the Imbue row is 179 and starts at has any " + err);
        Check(wtype != null && wtype.Label == "Weapon type" && wtype.ItemText == "Magic", "imbues: 353 is the weapon type row");

        // Any elemental rend, in one rule.
        LootItemRuleDraft b = LootItemRules.BuildBlank(new LootItemRuleOptions
        {
            BlankRule = true,
            Choices = Pick(("L:179", true, LootItemAttributeOp.HasAny, "17400")),
        });
        VTankLootCondition c = b.Rule.Conditions.Single();
        Eq(c.NodeType, VTankNodeTypes.LongValKeyFlagExists, "has any: Long Key Has Flag");
        Eq(string.Join(",", c.DataLines), "17400,179", "has any: the flags then the key");
        Eq(LootRuleText.Condition(c), "ImbuedEffect (imbue) has Any elemental rend", "has any: in words");
        bool Takes(LootItemFacts f) => LootFactsMatcher.Match(b.Rule.Conditions, f, true) == LootFactsVerdict.Match;
        Check(Takes(wand), "has any: a fire rend in ImbuedEffect2 counts");
        Check(!Takes(new LootItemFacts { ObjectClass = (int)AcObjectClass.MeleeWeapon, Ints = { [179] = 4 } }), "has any: armor rending alone is no elemental rend");
        Check(Takes(new LootItemFacts { ObjectClass = (int)AcObjectClass.MeleeWeapon, Ints = { [179] = 128 } }), "has any: cold rend counts");
        Eq(LootRuleText.ValueName(179, "17344"), "Any magical-element rend", "has any: acid, cold, electric, fire, nether");
    }

    private static void TestBlankRule()
    {
        LootItemRuleDraft b = LootItemRules.BuildBlank(new LootItemRuleOptions
        {
            BlankRule = true, Action = VTankLootAction.KeepUpTo, KeepCount = 5,
            Choices = Pick(("class", true, LootItemAttributeOp.Exactly, ((int)AcObjectClass.Jewelry).ToString()), ("L:105", true, LootItemAttributeOp.AtLeast, "7")),
        });
        Eq(b.Rule.Action, VTankLootAction.KeepUpTo, "blank rule: the action");
        Eq(b.Rule.KeepCount, 5, "blank rule: the keep count");
        Eq(b.Rule.Conditions.Count, 2, "blank rule: the ticked rows");
        Check(b.Rule.Name.StartsWith("Keep 5: Class is Jewelry"), "blank rule: named from the action and conditions: " + b.Rule.Name);
        LootItemRuleDraft f = LootItemRules.BuildBlank(new LootItemRuleOptions());
        Eq(f.Rule.Name, "Filter", "blank filter: still named Filter");
    }
}

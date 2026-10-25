using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Views;

namespace RynthInventory.Tests
{
    // "Find like this in Global Inventory" (/ginv like, 2026-10-24): the spec survives the trip,
    // the facts survive the file, and the matcher and the search page agree with the rule.
    internal static partial class Program
    {
        private static VTankLootCondition Cond(int type, params string[] lines) => new(type, "0", lines);

        private static void LikeFilterTests()
        {
            // A "like this" rule as the checklist makes it: class, workmanship, a spell, rating total.
            var conds = new List<VTankLootCondition>
            {
                Cond(VTankNodeTypes.ObjectClass, ((int)AcObjectClass.Jewelry).ToString()),
                Cond(VTankNodeTypes.LongValKeyGE, "7", "105"),
                Cond(VTankNodeTypes.SpellNameMatch, "^(Epic|Legendary) Strength$"),
                Cond(VTankNodeTypes.TotalRatingsGE, "3"),
            };
            string spec = LootFindSpec.Encode("like Gold Ring", conds);
            Check(spec.IndexOfAny(new[] { ' ', '\t', '\n', '+', '/', '=' }) < 0, "the spec is one chat word");
            Check(LootFindSpec.TryDecode(spec, out LootFindSpec back, out string err), "spec decodes: " + err);
            Eq(back.Label, "like Gold Ring", "label round trip");
            Eq(back.Conditions.Count, 4, "conditions round trip");
            Eq(back.Conditions[2].DataLines[0], "^(Epic|Legendary) Strength$", "regex line round trip");
            Check(!LootFindSpec.TryDecode("not a spec!", out _, out _), "garbage is refused");
            Check(!LootFindSpec.TryDecode(LootFindSpec.Encode("x", conds).Substring(1), out _, out _), "a cut spec is refused");

            // Facts survive the file.
            ItemRecord ring = Item(10, "Gold Ring", ItemCategory.Jewelry);
            ring.Identified = true;
            ring.HasFacts = true;
            ring.ObjectClass = (int)AcObjectClass.Jewelry;
            ring.Ints = new Dictionary<int, int> { [105] = 8, [370] = 2, [372] = 1 };
            ring.Doubles = new Dictionary<int, double> { [152] = 1.07 };
            ring.Spells = new uint[] { 1, 2 };
            ring.SpellNames = new[] { "Legendary Strength", "Major Focus" };
            var snap = Snap("Aelrynth", "tom", "Buffy", Now);
            snap.Items.Clear();
            snap.Items.Add(ring);
            CharacterSnapshot? read = InventoryJson.Read(InventoryJson.Write(snap), out string? jerr);
            Check(read != null, "facts file parses: " + jerr);
            ItemRecord r2 = read!.Items[0];
            Check(r2.HasFacts && r2.ObjectClass == (int)AcObjectClass.Jewelry, "class round trip");
            Eq(r2.Ints?[105] ?? 0, 8, "ints round trip");
            Eq(r2.Doubles?[152] ?? 0, 1.07, "floats round trip");
            Eq(read.ContentHash(), snap.ContentHash(), "hash round trip");
            ItemRecord changed = InventoryJson.Read(InventoryJson.Write(snap), out _)!.Items[0];
            changed.Ints![105] = 9;
            Check(CharacterSnapshot.MakeKey("a", "b") != null, "keys");
            var snap2 = Snap("Aelrynth", "tom", "Buffy", Now);
            snap2.Items.Clear();
            snap2.Items.Add(changed);
            Check(snap2.ContentHash() != snap.ContentHash(), "hash sees a fact change");

            // The matcher.
            var f = new LootItemFacts { Name = "Gold Ring", ObjectClass = (int)AcObjectClass.Jewelry };
            f.Ints[105] = 8; f.Ints[370] = 2; f.Ints[372] = 1;
            f.Spells.Add("Legendary Strength");
            Eq(LootFactsMatcher.Match(conds, f, true), LootFactsVerdict.Match, "all conditions hold");
            f.Ints[105] = 6;
            Eq(LootFactsMatcher.Match(conds, f, true), LootFactsVerdict.NoMatch, "workmanship too low");
            Eq(LootFactsMatcher.Match(conds, f, false), LootFactsVerdict.Unknown, "too low but not identified: unknown");
            f.ObjectClass = (int)AcObjectClass.Armor;
            Eq(LootFactsMatcher.Match(conds, f, false), LootFactsVerdict.NoMatch, "wrong class is a no even unidentified");
            Check(LootFactsMatcher.NeedsIdentify(conds), "workmanship needs an identify");
            Check(!LootFactsMatcher.NeedsIdentify(new[] { conds[0] }), "class alone doesn't");
            var book = new LootItemFacts { ObjectClass = (int)AcObjectClass.Scroll };
            Eq(LootFactsMatcher.Match(new[] { Cond(VTankNodeTypes.ObjectClass, "33") }, book, true), LootFactsVerdict.Match, "Book takes a Scroll (as the looter)");
            var named = new LootItemFacts { Name = "Copper Pea" };
            Eq(LootFactsMatcher.Match(new[] { Cond(VTankNodeTypes.StringValueMatch, LootItemRules.ExactPattern("copper pea"), "1") }, named, false),
                LootFactsVerdict.Match, "name match is case-insensitive and needs no identify");

            // The classifier.
            Eq(AcItemClassifier.Classify(AcItemClassifier.ItemTypeJewelry, "Ring", false, 0, 0), AcObjectClass.Jewelry, "jewelry");
            Eq(AcItemClassifier.Classify(AcItemClassifier.ItemTypeWritable, "Scroll of Foo", false, 0, 0), AcObjectClass.Scroll, "scroll by name");
            Eq(AcItemClassifier.Classify(AcItemClassifier.ItemTypeWritable, "Journal", true, 0, 0), AcObjectClass.Scroll, "scroll by spell");
            Eq(AcItemClassifier.Classify(AcItemClassifier.ItemTypeMisc, "Odd Blade", false, 0x00100000, 0), AcObjectClass.MeleeWeapon, "misc in a weapon slot");

            // The search page with the filter.
            LikeFilter? like = LikeFilter.Parse(spec, out string perr);
            Check(like != null, "LikeFilter parses: " + perr);
            ItemRecord plain = Item(11, "Silver Ring", ItemCategory.Jewelry);    // an old file: no facts
            plain.Identified = true;
            plain.ItemType = AcItemClassifier.ItemTypeJewelry;   // the class still comes from the item type
            ItemRecord armor = Item(12, "Leather Cap", ItemCategory.Armor);
            armor.HasFacts = true; armor.Identified = true; armor.ObjectClass = (int)AcObjectClass.Armor;
            var s3 = Snap("Aelrynth", "tom", "Buffy", Now);
            s3.Items.Clear();
            s3.Items.Add(r2); s3.Items.Add(plain); s3.Items.Add(armor);
            InventoryIndex index = InventoryIndex.Build(new[] { s3 }, null, "Aelrynth", "Buffy");
            InventoryResult res = index.Query(new InventoryQuery { Like = like!.Judge });
            Eq(res.Rows.Count, 1, "only the matching ring");
            Eq(res.Rows.Count > 0 ? res.Rows[0].Item.Name : "", "Gold Ring", "the gold ring");
            Eq(res.LikeUnknown, 1, "the ring saved without facts is unknown");
            Check(like.Lines.Count == 4 && like.Lines[0].Length > 0, "the conditions in words");
        }
    }
}

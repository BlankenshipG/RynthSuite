using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RynthCore.Loot.Editing;
using RynthCore.Plugin.RynthInventory;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Ui;
using RynthCore.Plugin.RynthInventory.Views;

namespace RynthInventory.Tests
{
    // Global Inventory's own attribute checklist (2026-10-24): with every group open and the
    // named rows ticked (their value lists drawn) the window still fits the engine's limits,
    // and ticking builds a filter that the search page uses.
    internal static partial class Program
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_checklist")]
        private static extern ref ChecklistFilter ChecklistOf(InventoryView v);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_showChecklist")]
        private static extern ref bool ShowChecklist(InventoryView v);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_openGroup")]
        private static extern ref string OpenGroup(ChecklistFilter f);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_choices")]
        private static extern ref Dictionary<string, LootEditItemChoice> Choices(ChecklistFilter f);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_extras")]
        private static extern ref Dictionary<string, LootEditItemExtra> Extras(ChecklistFilter f);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Rebuild")]
        private static extern void Rebuild(ChecklistFilter f);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_like")]
        private static extern ref LikeFilter? LikeOf(InventoryView v);

        private static void ChecklistTests()
        {
            // The standard lists.
            LootSpellCatalog cat = LootSpellCatalog.Build(RynthCore.Plugin.RynthInventory.Scanning.SpellTable.AllSpells());
            List<LootSpellFamily> families = cat.Families;
            List<string> cantrips = cat.Cantrips;
            Check(families.Exists(f => f.Name == "Strength" && f.School == LootSpellCatalog.Creature && f.MaxLevel == 8), "Strength is a creature family, I-VIII");
            Check(families.Exists(f => f.Name == "Armor" && f.School == LootSpellCatalog.Life), "Armor is life");
            Check(families.Exists(f => f.Name == "Impenetrability" && f.School == LootSpellCatalog.Item), "Impenetrability is item");
            Check(families.Exists(f => f.Name == "Void Magic Mastery"), "void magic mastery is offered");
            Check(!families.Exists(f => f.Name.Contains("Weakness") || f.Name.Contains("Bolt")), "no debuffs or war spells");
            Check(cantrips.Contains("Coordination"), "cantrips: Coordination");
            Console.WriteLine($"    spell lists: {families.Count} families, {cantrips.Count} cantrips");
            LootSpellFamily str = cat.Find("Strength")!;
            string p6 = str.Pattern(6, LootItemAttributeOp.SpellOrBetter);
            Check(p6.Contains("Strength Self VI") && p6.Contains("Incantation of Strength Self") && !p6.Contains("Strength Self V|"), "Strength VI or better lists VI, VII and VIII by name: " + p6);
            Check(new System.Text.RegularExpressions.Regex(str.Pattern(7, LootItemAttributeOp.SpellOrBetter), System.Text.RegularExpressions.RegexOptions.IgnoreCase).IsMatch("Might of the Lugians"), "level VII is matched by its own name");
            Eq(str.Pattern(6, LootItemAttributeOp.SpellExact), "^(Strength Self VI|Strength Other VI)$", "family exact");

            // The window with the checklist open: every group, the named rows ticked.
            InventoryContext c = Context(true);
            var view = new InventoryView(c);
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthInventory/Main", "Global Inventory") { Visible = true });
            view.Tab = InventoryView.TabSearch;
            ShowChecklist(view) = true;
            ChecklistFilter cl = ChecklistOf(view);
            OpenGroup(cl) = "Spells";   // the largest group (the pickers)
            foreach (string id in new[] { "class", "L:131", "L:166", "L:159", "L:48", "L:353", "L:45", "L:158", "L:105" })
                Choices(cl)[id] = new LootEditItemChoice { Id = id, On = true, Op = -1, Value = string.Empty };
            Choices(cl)["class"].Value = ((int)RynthCore.Loot.AcObjectClass.Jewelry).ToString();
            Choices(cl)["L:105"].Value = "7";
            var x = new LootEditItemExtra { Kind = LootEditItemExtra.Cantrip, Name = "Coordination", Level = 3 };
            Extras(cl)[LootItemAttributes.ExtraId(x)] = x;
            Rebuild(cl);
            Draw(w, view);   // takes the checklist's rule
            Draw(w, view);
            int worst = 0, worstBytes = 0;
            foreach (string g in new[] { "Item", "Weapon / Armor", "Ratings", "Spells", "Pet essence", "T11", "Requirements" })
            {
                OpenGroup(cl) = g;
                Draw(w, view);
                ParseAll(host, "checklist " + g, out int ops);
                worst = Math.Max(worst, ops);
                worstBytes = Math.Max(worstBytes, w.Body.Length);
                w.MarkSubmitted(true);
            }
            Check(worst <= 1200, $"checklist open: {worst} ops");
            Check(worstBytes < 48 * 1024, $"checklist open: {worstBytes} bytes");
            Console.WriteLine($"    checklist, worst group open: {worst} ops, {worstBytes} bytes");
            LikeFilter? like = LikeOf(view);
            Check(like != null && like.Lines.Any(l => l.Contains("Jewelry")) && like.Lines.Count == cl.Conditions.Count, "ticking makes the window's filter");
            Check(cl.Conditions.Any(cd => cd.NodeType == RynthCore.Loot.VTank.VTankNodeTypes.SpellNameMatch && cd.DataLines[0].StartsWith("^(Legendary|Paragon")), "the cantrip from the list, or better");
            w.MarkSubmitted(true);

            // The details of a selected item list its full saved attributes.
            InventoryContext c2 = Context(false);
            var view2 = new InventoryView(c2);
            var host2 = new UiHost();
            UiWindow w2 = host2.Add(new UiWindow("RynthInventory/Main", "Global Inventory") { Visible = true });
            view2.Tab = InventoryView.TabSearch;
            string key = RowKeyOfFirst(c2);
            InventoryRow first = c2.Index!.Rows.First(r => key.EndsWith("|" + r.Item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) && key.StartsWith(r.Owner.Key));
            first.Item.HasFacts = true;
            first.Item.Identified = true;
            first.Item.ObjectClass = (int)RynthCore.Loot.AcObjectClass.Jewelry;
            first.Item.Ints = new Dictionary<int, int> { [105] = 8, [131] = 1, [370] = 3, [158] = 2, [160] = 150 };
            first.Item.Doubles = new Dictionary<int, double> { [144] = 1.05 };
            Draw(w2, view2);
            Apply(host2, Event(1, 10, w2.Hash, w2.KeyOf("r." + key), new byte[] { 1, 0 }));
            Draw(w2, view2);
            ParseAll(host2, "details with attributes", out int dops);
            string body = BodyText(w2);
            Check(body.Contains("class Jewelry") && body.Contains("workmanship 8") && body.Contains("Ratings:") && body.Contains("damage rating 3"),
                "details list the full attributes");
            Check(dops <= 1500, $"details with attributes: {dops} ops");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// ACECustom's newer T11-era loot items (2026-11-11): the "T11 item" keys and the checklist rows.
internal static partial class Program
{
    private static void RunT11ItemTests()
    {
        Console.WriteLine("\n-- T11 items: bounties, school gems, infusion gems, tokens --");
        const string bounty = "Kill 250 Banderlings in Tier 15. Only your own kills count, and only in Tier 15. Reward: 5 x Bounty Reward Token, paid to your pack automatically when done. Use it on a ring or bracelet you have to start it; /bounty shows your slots.";
        Eq(T11Items.KindOf(0, bounty), T11Items.ItemBounty, "t11 item: a bounty by its description");
        Eq(T11Items.TierOf(T11Items.ItemBounty, bounty, 0), 15, "t11 item: the bounty's tier");
        Eq(T11Items.TierOf(T11Items.ItemBounty, "Kill 400 creatures in Valley of Death. Use it on a ring or bracelet you have to start it", 0), 0, "t11 item: a region bounty has no tier");
        Eq(T11Items.KindOf(777700057, null), T11Items.SchoolGem, "t11 item: a school gem by WCID");
        Eq(T11Items.TierOf(T11Items.SchoolGem, null, 17), 17, "t11 item: the gem's earned tier");
        Eq(T11Items.KindOf(777700094, null), T11Items.InfusionGem, "t11 item: an infusion gem");
        Eq(T11Items.CoinsOf(777700094), 100, "t11 item: its coins");
        Eq(T11Items.KindOf(777700038, null), T11Items.LevelledCharm, "t11 item: Improved Cleave is a levelled charm");
        Eq(T11Items.KindOf(777708010, null), T11Items.BountyRewardToken, "t11 item: a bounty reward token");
        Eq(T11Items.KindOf(99980078, null), T11Items.EventLumToken, "t11 item: an event lum token");
        Eq(T11Items.KindOf(78780032, null), T11Items.UniversalQuiver, "t11 item: the Universal Quiver Charm");
        Eq(T11Items.KindOf(273, "A pile of pyreals."), T11Items.None, "t11 item: anything else");

        // A clicked school gem: "T11 item is School gem" ticked, its tier offered.
        var f = new LootItemFacts { Name = "School Gem", ObjectClass = (int)AcObjectClass.Gem };
        f.Ints[T11Keys.ItemKind] = T11Items.SchoolGem;
        f.Ints[T11Keys.ItemTier] = 16;
        LootItemRuleDraft? d = LootItemRules.Build(f, new LootItemRuleOptions(), out string err);
        Check(d != null, "t11 item: a rule for a school gem builds " + err);
        if (d != null)
        {
            var conds = d.Rule.Conditions.Select(c => $"{c.NodeType}:{string.Join(",", c.DataLines)}").ToList();
            Check(conds.Any(c => c.EndsWith("," + T11Keys.ItemKind) && c.Contains(":" + T11Items.SchoolGem + ",")), "t11 item: the kind is required: " + string.Join(" ; ", conds));
            Check(!conds.Any(c => c.EndsWith("," + T11Keys.ItemTier)), "t11 item: the tier is offered, not ticked");
            Check(LootItemRules.PreviewLines(d.Rule).Any(l => l.Contains("School gem")), "t11 item: the preview names the kind");
        }
        // The blank checklist (Add rule) has the rows too.
        var blank = LootItemAttributes.ForBlank();
        Check(blank.Exists(r => r.Id == "t11:itemkind") && blank.Exists(r => r.Id == "t11:itemtier"), "t11 item: Add rule offers the T11 item rows");
    }
}

using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // The T11 Tracking tab (2026-11-11): ACECustom's ItemBountyDisplay and a levelled charm's appraisal.
    internal static partial class Program
    {
        private static void T11TrackingTests()
        {
            var slots = T11Tracking.ParseDisplay(
                "v2|R1,R2,B1,B2;R1|15|2|37.5|250|Banderlings|777708003|5|1|0|0|Bounty: Banderling Cull|Bounty Reward Token|Double click to bank it.|T15|Tier 15;"
                + "R2|0|1|12|400||777708001|1|0|0|0|Valley Sweep|Bounty Reward Token||VoD|Valley of Death;B1|empty;B2|empty");
            Check(slots.Count == 4, "t11: four slots");
            Check(slots[0].Key == "R1" && slots[0].Tier == 15 && slots[0].Species && slots[0].Target == "Banderlings"
                  && slots[0].Kills == 250 && System.Math.Abs(slots[0].Progress - 37.5) < 0.001 && slots[0].RewardAmount == 5,
                  "t11: a species bounty in a tier");
            Check(slots[0].Task == "Kill 250 Banderlings in Tier 15" && slots[0].Reward == "5 x Bounty Reward Token", "t11: its task and reward: " + slots[0].Task);
            Check(slots[1].PlaceShort == "VoD" && !slots[1].Species && slots[1].Task == "Kill 400 creatures in Valley of Death", "t11: a region sweep: " + slots[1].Task);
            Check(slots[2].Empty && slots[3].Key == "B2" && slots[3].Empty, "t11: empty slots");
            Check(T11Tracking.ParseDisplay("").Count == 0 && T11Tracking.ParseDisplay("junk").Count == 0, "t11: nothing from nothing");

            var c = T11Tracking.ParseCharm("Charm [Level 41 (95% to 42)]\nPrestige Coins put in: 14,210 - next level in 46\n\nStatus: ON");
            Check(c.Known && c.Level == 41 && c.Pct == 95 && c.Coins == 14210 && c.ToNext == 46, "t11: a levelled charm's level and coins");
            var max = T11Tracking.ParseCharm("Charm [Level 100 (max)]\nPrestige Coins put in: 2,000,000");
            Check(max.Known && max.Level == 100 && max.ToNext == 0 && max.Coins == 2000000, "t11: at the top");
            Check(!T11Tracking.ParseCharm("Charm [Tier 2/3]").Known, "t11: a tiered charm isn't levelled");
            Check(T11Tracking.InfusionGems[777700097] == 5000 && T11Tracking.IsLumToken(777708004) && T11Tracking.IsLumToken(99980079), "t11: the gem and token tables");
        }
    }
}

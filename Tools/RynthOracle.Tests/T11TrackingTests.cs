using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // The T11 Tracking tab (2026-11-11; the ILT Tracker since 2026-11-21): ACECustom's ItemBountyDisplay and a levelled charm's appraisal.
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
            QuestBonusTests();
        }

        // The ILT Tracker's quest bonus (2026-11-21): ACECustom's /bonus and /qb lines, and the bonus levels.
        private static void QuestBonusTests()
        {
            Check(RynthCore.Plugin.RynthOracle.Views.OracleView.TabT11 == "ILT Tracker", "qb: the T11 Tracking tab is the ILT Tracker");
            var t = new System.DateTime(2026, 11, 21, 20, 0, 0, System.DateTimeKind.Utc);
            var qb = new QuestBonus();
            Check(qb.OnChatLine("[BONUS] Your XP multiplier from Quests is: x2.35", t) && !qb.Bonus.Seen, "qb: a /bonus line is read, the reading waits for Total");
            qb.OnChatLine("[BONUS] Your XP multiplier from Equipment is: x1.10", t);
            qb.OnChatLine("[BONUS] Your XP multiplier from Enlightenment is: x3.00", t);
            Check(qb.OnChatLine("[BONUS] Your Total XP multiplier is: x7.76", t) && qb.Bonus.Seen
                  && qb.Bonus.Quests == 2.35 && qb.Bonus.Equipment == 1.10 && qb.Bonus.Enlightenment == 3.00 && qb.Bonus.Total == 7.76,
                  "qb: the four /bonus multipliers");
            Check(QuestBonus.QbFromMultiplier(2.35) == 270 && QuestBonus.QbFromMultiplier(1) == 0, "qb: x2.35 from quests is 270 QB");
            Check(qb.OnChatLine("Your current quest bonus count: 1,234", t) && qb.ChatCount == 1234, "qb: the /qb count line");
            Check(!qb.OnChatLine("You say, \"[BONUS] Your Total XP multiplier is: x99\"", t) && qb.Bonus.Total == 7.76, "qb: chat that quotes it is not read");
            Check(System.Math.Abs(QuestBonus.Multiplier(270) - 2.35) < 1e-9 && QuestBonus.QbPerLevel == 50, "qb: 0.5% XP a QB, a level every 50 QB");
            var (cur, next, frac, toNext) = QuestBonus.Levels(270);
            Check(cur.Level == 5 && next[0].Level == 6 && next[0].Qb == 300 && System.Math.Abs(next[0].Multiplier - 2.5) < 1e-9
                  && toNext == 30 && System.Math.Abs(frac - 0.4f) < 1e-6 && next.Count == 4, "qb: 270 QB is level 5, 30 QB to level 6 (x2.50)");
            Check(qb.BestCount(null, out string src) == 1234 && src == "/qb", "qb: without RynthILT the /qb count in chat");
            var ilt = new IltQuestBonusList(false, 1, 300, "", new[] { "a", "Dynamic_1_x" }, new[] { "b" });
            Check(qb.BestCount(ilt, out _) == 300 && QuestBonus.IsStamp("Dynamic_1_x") && !QuestBonus.IsStamp("a"), "qb: RynthILT's count first; stamps");
            int s0 = qb.Session;
            qb.Clear();
            Check(!qb.Bonus.Seen && qb.ChatCount == -1 && qb.Session == s0 + 1 && qb.BestCount(null, out _) == -1, "qb: logout forgets it");
            Check(IltWikiGuide.IsGuidePage(IltWikiGuide.BotPasswordsUrl) && !IltWikiGuide.IsGuidePage("https://example.com") && IltWikiGuide.Steps.Length == 6, "qb: the wiki guide opens only its own pages");
        }
    }
}

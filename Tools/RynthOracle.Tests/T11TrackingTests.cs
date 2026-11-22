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
            Check(System.Math.Abs(QuestBonus.Multiplier(270) - 2.35) < 1e-9, "qb: 0.5% XP a QB");
            // QB Deeds (ACECustom DeedsTiers): a tier every 1,000 QB, 15 in all.
            Check(QbDeeds.Tiers.Length == 15 && QbDeeds.Tiers[0].Qb == 1000 && QbDeeds.Tiers[14].Qb == 15000, "deeds: 15 tiers, 1,000 QB apart");
            Check(QbDeeds.TierFor(999) == 0 && QbDeeds.TierFor(1000) == 1 && QbDeeds.TierFor(12345) == 12 && QbDeeds.TierFor(99999) == 15,
                  "deeds: 999 QB no tier, 1,000 tier 1, 12,345 tier 12, the top is 15");
            var (nt, nf, nl) = QbDeeds.Progress(12345);
            Check(nt == 13 && nl == 655 && System.Math.Abs(nf - 0.345f) < 1e-4, "deeds: 12,345 QB is 655 QB from tier 13");
            Check(QbDeeds.Progress(15000).Next == 0 && QbDeeds.Progress(0).Next == 1 && QbDeeds.Progress(0).ToNext == 1000, "deeds: the top, and nothing yet");
            Check(System.Math.Abs(QbDeeds.DamageBonus(2)) < 1e-9 && System.Math.Abs(QbDeeds.DamageBonus(9) - 0.06) < 1e-9
                  && System.Math.Abs(QbDeeds.DamageBonus(15) - 0.10) < 1e-9, "deeds: +2% damage at 3, 6, 9, 12, 15 (10% at the top)");
            Check(QbDeeds.Line(QbDeeds.Tiers[8]).EndsWith("+2% damage (6% in all)") && QbDeeds.Tiers[9].Summary.StartsWith("/rebuff"),
                  "deeds: tier 9's damage step, tier 10's /rebuff");
            var prof = IltServerProfile.Parse("{\"ilt\":true,\"world\":\"Infinite Leaftide\",\"features\":[],\"deeds\":{\"loaded\":true,\"unsupported\":false,"
                + "\"busy\":false,\"qb\":12345,\"on\":true,\"unlocked\":12,\"nextAt\":13000,\"status\":\"\",\"loadedUtc\":\"2026-11-21T21:30:00Z\","
                + "\"tiers\":[{\"n\":1,\"qb\":1000,\"unlocked\":true,\"rewards\":[\"Legendary Strength\"]},{\"n\":13,\"qb\":13000,\"unlocked\":false,\"rewards\":[]}]}}");
            IltDeedsInfo? dd = prof.Deeds;
            Check(dd != null && dd.Loaded && dd.On == true && dd.Qb == 12345 && dd.Unlocked == 12 && dd.NextAt == 13000 && dd.Tiers.Count == 2
                  && dd.Tiers[0].Rewards[0] == "Legendary Strength" && !dd.Tiers[1].Unlocked && dd.LoadedUtc.Hour == 21, "deeds: RynthILT's /deeds read from the profile");
            Check(qb.BestCount(null, out _, dd) == 12345, "deeds: the /deeds QB first");
            Check(IltServerProfile.Parse("{\"ilt\":true,\"features\":[]}").Deeds == null, "deeds: an older RynthILT has none");
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

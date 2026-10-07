using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Server Features gate + the infinite-attribute raiser (2026-10-06). The gate decides from
// Settings > Misc (world-name patterns, manual override) whether the ILT features ask the
// server at all; the raiser reads "/xp all" costs and spends unassigned XP with "/attr <abbr> 1"
// on the ticked stats, in the chosen order, never below the reserve.
internal static class AttributeRaiserTests
{
    private const uint Player = 0x50000C01;

    public static void Register(Runner r)
    {
        r.Add("server features: name patterns (exact, wildcards, case)", Patterns);
        r.Add("server features: list parsing and the override", ListAndOverride);
        r.Add("attr raiser: pick by mode (priority, round robin, cheapest)", PickModes);
        r.Add("attr raiser: pick respects ticks, reserve and unknown costs", PickLimits);
        r.Add("attr raiser: order repair and stat names from server lines", OrderAndNames);
        r.Add("attr raiser: a run reads costs, raises in order, stops when nothing is affordable", FullRun);
        r.Add("attr raiser: off when the world is not in the Server Features list", GatedOff);
        r.Add("attr raiser: safety (stale costs, another stat's reply, gate off mid-run, held off)", Safety);
        r.Add("attr raiser: by-hand +1 / +10 priced like UB, refused when unaffordable, costs read on tab open", ByHandPriced);
        r.Add("aug planner: /aug pulled on tab open, totals, reset targets, copy texts", AugPlanner);
    }

    private static void ByHandPriced()
    {
        Check.Eq(IltAttributeRaiser.ProjectCost(1_000, 1), 1_000L, "one level costs the reported cost");
        // 1000 * (1.077^10 - 1) / 0.077 = 14,281.7 (UB's EstimateMultiLevelCost).
        Check.Eq(IltAttributeRaiser.ProjectCost(1_000, 10), 14_281L, "ten levels grow 7.7% each");
        Check.Eq(IltAttributeRaiser.ProjectCost(0, 10), 0L, "unknown cost projects nothing");
        Check.Eq(IltAttributeRaiser.ProjectCost(long.MaxValue / 2, 10), long.MaxValue, "saturates instead of overflowing");

        var settings = new LegacyUiSettings();
        var hub = NewHub("InfiniteLeaftide", settings, "attr-byhand", out _);
        var raiser = hub.Progression.Attributes;
        FakeHost.Quads[(Player, IltInventory.QuadAvailableXp)] = 5_000;
        // Levels already known (as after the login probe), so the tab doesn't queue /aug ahead of /xp all.
        hub.Progression.ApplyAugLines(new List<string> { "Creature: 1" });

        // Opening the tab (a snapshot poll) reads the costs once per login.
        var sb = new System.Text.StringBuilder();
        hub.Progression.AppendSnapshotJson(sb);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/xp all")), "the first snapshot reads the costs");
        Reply(hub, "[XP] Your XP cost for next 1 Coordination level is: 1,000",
                   "[XP] Your XP cost for next 1 Strength level is: 4,000");
        TickUntil(hub, () => false, 400);
        int reads = FakeHost.ChatCommands.Count(x => x == "/xp all");
        hub.Progression.AppendSnapshotJson(new System.Text.StringBuilder());
        TickUntil(hub, () => false, 400);
        Check.Eq(FakeHost.ChatCommands.Count(x => x == "/xp all"), reads, "later snapshots don't read again");

        sb.Clear();
        raiser.AppendJson(sb);
        string json = sb.ToString();
        System.Text.Json.JsonDocument.Parse("{" + json + "}").Dispose();
        Check.True(json.Contains("\"key\":\"coo\"") && json.Contains("\"afford1\":true") && json.Contains("\"cost10\":\"~14.3K\""),
            $"Coordination: +1 affordable, +10 projected (got {json})");
        Check.True(json.Contains("\"updated\":\"") && !json.Contains("\"updated\":\"-\""), "the last-updated time is set");

        // +10 Coordination (~14.3K of 5K) is refused; +1 Strength (4K of 5K) goes out.
        raiser.RaiseManual("coo", 10);
        var status = new System.Text.StringBuilder();
        raiser.AppendJson(status);
        Check.True(status.ToString().Contains("Not enough unassigned XP"), "an unaffordable +10 is refused with the reason");
        raiser.RaiseManual("str", 1);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/attr str 1")), "an affordable +1 is sent");
        Check.False(FakeHost.ChatCommands.Contains("/attr coo 10"), "the refused +10 was never sent");
    }

    private static void AugPlanner()
    {
        var settings = new LegacyUiSettings();
        var hub = NewHub("InfiniteLeaftide", settings, "aug-planner", out _);
        var prog = hub.Progression;

        prog.AppendSnapshotJson(new System.Text.StringBuilder());
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/aug")), "opening the tab pulls /aug once");
        prog.ApplyAugLines(new List<string> { "Creature: 100", "Specialize: 2", "Summon: 0" });

        prog.HandleRemote(new[] { "augtarget", "creature", "110" });
        var sb = new System.Text.StringBuilder();
        prog.AppendSnapshotJson(sb);
        string json = sb.ToString();
        using (var doc = System.Text.Json.JsonDocument.Parse("{" + json + "}"))
        {
            var aug = doc.RootElement.GetProperty("aug");
            Check.Eq(aug.GetProperty("totCur").GetInt64(), 102L, "totals: current");
            Check.Eq(aug.GetProperty("totTgt").GetInt64(), 112L, "totals: target");
            Check.Eq(aug.GetProperty("totInc").GetInt64(), 10L, "totals: increase");
            Check.Eq(aug.GetProperty("totCoins").GetInt64(), 30L, "Creature costs 3 coins a level");
            var creature = aug.GetProperty("rows").EnumerateArray().First(x => x.GetProperty("key").GetString() == "creature");
            Check.Eq(creature.GetProperty("inc").GetInt32(), 10, "row: increase");
            string discord = aug.GetProperty("copyDiscord").GetString() ?? "";
            Check.True(discord.StartsWith("```", StringComparison.Ordinal) && discord.Contains("TOTALS:") && discord.Contains("Creature"),
                "Discord copy is a code-block table with totals");
            Check.True(discord.All(ch => ch < 128), "copy text is ASCII (the engine reads the snapshot as ANSI)");
            string ingame = aug.GetProperty("copyIngame").GetString() ?? "";
            Check.True(ingame.Contains("Crit: 100") && ingame.Contains("Spec: 2") && !ingame.Contains("Sum:"),
                $"in-game copy lists current levels with short names (got '{ingame}')");
        }

        prog.HandleRemote(new[] { "augreset" });
        Check.Eq(hub.State.Character.AugTargets["creature"], 100, "reset targets: back to current");
        Check.Eq(hub.State.Character.AugTargets["summon"], 0, "reset targets: unloaded augs go to 0");
        Check.Eq(FakeHost.ChatCommands.Count(x => x == "/aug"), 1, "levels loaded: no second automatic /aug");
    }

    private static void Patterns()
    {
        Check.True(ServerFeatureGate.Matches("InfiniteLeaftide", "InfiniteLeaftide"), "exact");
        Check.True(ServerFeatureGate.Matches("infiniteleaftide", "InfiniteLeaftide"), "exact ignores case");
        Check.False(ServerFeatureGate.Matches("InfiniteLeaftide2", "InfiniteLeaftide"), "exact means the whole name");
        Check.True(ServerFeatureGate.Matches("Leaftide Test", "*Leaftide*"), "contains");
        Check.True(ServerFeatureGate.Matches("MyLeaftide", "*tide"), "ends with");
        Check.True(ServerFeatureGate.Matches("Leaftide-PTR", "Leaf*"), "starts with");
        Check.True(ServerFeatureGate.Matches("Aelrynth Infinite", "Ael*Inf*"), "two wildcards");
        Check.False(ServerFeatureGate.Matches("Aelrynth", "*Leaftide*"), "no match");
        Check.False(ServerFeatureGate.Matches("ab", "a*bb"), "the end part must fit after the start");
        Check.True(ServerFeatureGate.NameMatches("Aelrynth", new[] { "", "InfiniteLeaftide", "aelrynth" }, out string m) && m == "aelrynth",
            "NameMatches reports the pattern that matched");
        Check.False(ServerFeatureGate.NameMatches("", ServerFeatureGate.DefaultServerNames, out _), "an unknown world never matches");
    }

    private static void ListAndOverride()
    {
        var list = ServerFeatureGate.ParseList(" InfiniteLeaftide , *Leaftide*;Aelrynth,,aelrynth ");
        Check.Eq(string.Join("|", list), "InfiniteLeaftide|*Leaftide*|Aelrynth", "trimmed, split on , and ;, de-duplicated ignoring case");
        Check.Eq(ServerFeatureGate.FormatList(list), "InfiniteLeaftide, *Leaftide*, Aelrynth", "formatted back for the text box");

        var s = new LegacyUiSettings();
        Check.True(s.FeatureServerNames.Contains("InfiniteLeaftide"), "a fresh profile starts with the defaults");
        Check.True(ServerFeatureGate.IsEnabled("InfiniteLeaftide", s), "the default list covers InfiniteLeaftide");
        Check.False(ServerFeatureGate.IsEnabled("Aelrynth", s), "another world is off by default");
        s.ForceServerFeatures = true;
        Check.True(ServerFeatureGate.IsEnabled("Aelrynth", s), "the manual override turns it on anywhere");
        s.ForceServerFeatures = false;
        s.FeatureServerNames = new List<string> { "Aelrynth" };
        Check.True(ServerFeatureGate.IsEnabled("aelrynth", s), "a world added to the list is on");
        Check.False(ServerFeatureGate.IsEnabled("InfiniteLeaftide", s), "a world removed from the list is off");
        Check.True(ServerFeatureGate.IsEnabled("InfiniteLeaftide", null), "no profile yet: the defaults apply");
    }

    private static readonly string[] Order = { "coo", "qui", "str", "end", "foc", "sel", "hea", "sta", "man" };

    private static void PickModes()
    {
        var cost = new Dictionary<string, long> { ["coo"] = 500, ["qui"] = 300, ["str"] = 100 };
        var ticked = new HashSet<string> { "coo", "qui", "str" };
        int rr = 0;
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, ticked, cost, 1000, ref rr), "coo",
            "priority: the first affordable stat in the order");
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, ticked, cost, 400, ref rr), "qui",
            "priority: skips a stat it can't afford");
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Cheapest, Order, ticked, cost, 1000, ref rr), "str",
            "cheapest: the lowest next-level cost");
        var tie = new Dictionary<string, long> { ["coo"] = 100, ["str"] = 100 };
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Cheapest, Order, ticked, tie, 1000, ref rr), "coo",
            "cheapest: a tie goes to the higher-priority stat");

        rr = 0;
        var picks = new List<string?>();
        for (int i = 0; i < 4; i++) picks.Add(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.RoundRobin, Order, ticked, cost, 1000, ref rr));
        Check.Eq(string.Join(",", picks), "coo,qui,str,coo", "round robin: one each in order, then around again");
        rr = 0;
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.RoundRobin, Order, ticked, cost, 400, ref rr), "qui",
            "round robin: an unaffordable stat is passed over");
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.RoundRobin, Order, ticked, cost, 400, ref rr), "str",
            "round robin: carries on from the last pick");
    }

    private static void PickLimits()
    {
        var cost = new Dictionary<string, long> { ["coo"] = 500, ["qui"] = 300 };
        int rr = 0;
        Check.Null(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, new HashSet<string>(), cost, 10_000, ref rr),
            "nothing ticked: nothing raised");
        Check.Eq(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, new HashSet<string> { "qui" }, cost, 10_000, ref rr), "qui",
            "an unticked stat is never spent on, even at the top of the order");
        Check.Null(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, new HashSet<string> { "coo", "qui" }, cost, 0, ref rr),
            "no budget (XP at or below the reserve): nothing raised");
        Check.Null(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Priority, Order, new HashSet<string> { "str" }, cost, 10_000, ref rr),
            "a stat whose cost wasn't read is not raised blind");
        var zero = new Dictionary<string, long> { ["coo"] = 0 };
        Check.Null(IltAttributeRaiser.Pick(IltAttributeRaiser.Mode.Cheapest, Order, new HashSet<string> { "coo" }, zero, 10_000, ref rr),
            "a zero cost (unparsed) is not raised");
    }

    private static void OrderAndNames()
    {
        var repaired = IltAttributeRaiser.RepairOrder(new List<string> { "sel", "bogus", "Self", "Strength", "hea" });
        Check.Eq(string.Join(",", repaired), "sel,str,hea,coo,qui,end,foc,sta,man",
            "saved order kept, duplicates/junk dropped, missing stats appended in the default order");
        Check.Eq(IltAttributeRaiser.RepairOrder(null).Count, 9, "a missing order becomes the default");
        Check.Eq(string.Join(",", IltAttributeRaiser.RepairSelection(new List<string> { "Coordination", "coo", "x" })), "coo",
            "selection: names map to abbreviations, duplicates and junk dropped");

        Check.Eq(IltAttributeRaiser.Find("MaxHealth")?.Abbr, "hea", "cost line 'MaxHealth'");
        Check.Eq(IltAttributeRaiser.Find("Maximum Stamina")?.Abbr, "sta", "'Maximum Stamina'");
        Check.Eq(IltAttributeRaiser.Find("Self")?.Abbr, "sel", "Self");
        Check.Eq(IltAttributeRaiser.Find("qui")?.Abbr, "qui", "abbreviation");
        Check.Null(IltAttributeRaiser.Find("Luck"), "unknown names are rejected");

        var cost = IltParse.XpCost.Match("[XP] Your XP cost for next 1 Coordination level is: 12,345,678");
        Check.True(cost.Success && cost.Groups[1].Value == "Coordination" && IltParse.ParseLeadingLong(cost.Groups[2].Value) == 12_345_678,
            "the /xp all cost line parses");
        var raised = IltParse.AttrRaised.Match("[ATTR] Quickness attribute raised by 1, costing 9,876!");
        Check.True(raised.Success && raised.Groups[1].Value == "Quickness" && raised.Groups[2].Value == "1"
                   && IltParse.ParseLeadingLong(raised.Groups[3].Value) == 9876, "the /attr success line parses");
    }

    // ── The loop on the fake host ─────────────────────────────────────────────

    private static IltHubController NewHub(string world, LegacyUiSettings settings, string folder, out RynthCoreHost host)
    {
        FakeHost.Reset();
        FakeHost.WorldName = world;
        host = FakeHost.Create(Player);
        string dir = Path.Combine(Program.TempRoot, folder);
        Directory.CreateDirectory(dir);
        return new IltHubController(host, dir, () => null, () => settings, () => null, () => { });
    }

    /// <summary>Ticks the hub until <paramref name="done"/> or the timeout (the chat queue spaces sends by real time).</summary>
    private static bool TickUntil(IltHubController hub, Func<bool> done, int timeoutMs = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            hub.Tick();
            if (done()) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static void Reply(IltHubController hub, params string[] lines)
    {
        foreach (string l in lines) hub.OnChat(l);
    }

    private static void FullRun()
    {
        var settings = new LegacyUiSettings();
        var hub = NewHub("InfiniteLeaftide", settings, "attr-run", out _);
        var c = hub.State.Character;
        c.AttrRaiseStats = new List<string> { "coo", "qui" };
        c.AttrRaiseMode = (int)IltAttributeRaiser.Mode.Priority;
        c.AttrKeepXp = 200;
        FakeHost.Quads[(Player, IltInventory.QuadAvailableXp)] = 2_500;
        var raiser = hub.Progression.Attributes;

        Check.Null(raiser.Blocker(), "an ILT world with /xp not reported off: the raiser can run");
        raiser.Start(auto: false);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/xp all")), "a run starts by reading the costs");
        Reply(hub, "[XP] Your XP cost for next 1 Strength level is: 50",
                   "[XP] Your XP cost for next 1 Coordination level is: 1,000",
                   "[XP] Your XP cost for next 1 Quickness level is: 1,200");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/attr coo 1")),
            "priority: Coordination first (Strength is cheaper but not ticked)");

        FakeHost.Quads[(Player, IltInventory.QuadAvailableXp)] = 1_500;
        Reply(hub, "[ATTR] Coordination attribute raised by 1, costing 1,000!");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Count(x => x == "/xp all") >= 2), "costs are read again after each raise");
        Reply(hub, "[XP] Your XP cost for next 1 Coordination level is: 2,000",
                   "[XP] Your XP cost for next 1 Quickness level is: 1,200");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/attr qui 1")),
            "Coordination now costs more than the spendable 1,300: Quickness next");

        FakeHost.Quads[(Player, IltInventory.QuadAvailableXp)] = 300;
        Reply(hub, "[ATTR] Quickness attribute raised by 1, costing 1,200!");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Count(x => x == "/xp all") >= 3), "third cost read");
        Reply(hub, "[XP] Your XP cost for next 1 Coordination level is: 2,000",
                   "[XP] Your XP cost for next 1 Quickness level is: 1,500");
        Check.True(TickUntil(hub, () => !raiser.Running), "nothing affordable above the reserve: the run ends");
        Check.Eq(FakeHost.ChatCommands.Count(x => x.StartsWith("/attr", StringComparison.Ordinal)), 2, "exactly two raises sent");
        string summary = FakeHost.Chat.LastOrDefault(x => x.Contains("Attribute raise")) ?? "";
        Check.True(summary.Contains("Coordination +1") && summary.Contains("Quickness +1") && summary.Contains("2.2K"),
            $"the run reports what it raised and spent (got '{summary}')");

        var sb = new System.Text.StringBuilder();
        raiser.AppendJson(sb);
        string json = sb.ToString();
        Check.True(json.StartsWith("\"attr\":{\"off\":false", StringComparison.Ordinal), "the Progression snapshot carries the raiser");
        Check.True(json.Contains("\"key\":\"coo\"") && json.Contains("\"cost\":\"2.0K\""), "rows carry the last read costs");
        System.Text.Json.JsonDocument.Parse("{" + json + "}").Dispose();   // throws if the JSON is malformed

        // Panel commands: order, ticks, mode, reserve, a manual +10.
        raiser.HandleRemote("attrmove", new[] { "attrmove", "qui", "up" });
        Check.Eq(c.AttrOrder[0], "qui", "attrmove up");
        raiser.HandleRemote("attron", new[] { "attron", "str", "on" });
        Check.True(c.AttrRaiseStats.Contains("str"), "attron");
        raiser.HandleRemote("attrmode", new[] { "attrmode", "2" });
        Check.Eq(c.AttrRaiseMode, 2, "attrmode");
        raiser.HandleRemote("attrkeep", new[] { "attrkeep", "1.5b" });
        Check.Eq(c.AttrKeepXp, 1_500_000_000L, "attrkeep takes k/m/b suffixes");
        raiser.HandleRemote("attrraise", new[] { "attrraise", "foc", "10" });
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/attr foc 10")), "a manual +10 sends /attr foc 10");
    }

    private static void Safety()
    {
        var settings = new LegacyUiSettings();
        var hub = NewHub("InfiniteLeaftide", settings, "attr-safety", out _);
        var c = hub.State.Character;
        var raiser = hub.Progression.Attributes;
        c.AttrRaiseStats = new List<string> { "coo", "qui" };
        FakeHost.Quads[(Player, IltInventory.QuadAvailableXp)] = 10_000;

        // A cost seen earlier (passively, here by a hand-typed /xp all) is not trusted by a run:
        // only the costs in the run's own read count.
        Reply(hub, "[XP] Your XP cost for next 1 Coordination level is: 100");
        raiser.Start(auto: false);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/xp all")), "run reads costs");
        Reply(hub, "[XP] Your XP cost for next 1 Quickness level is: 500");   // Coordination missing this time
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Any(x => x.StartsWith("/attr", StringComparison.Ordinal))), "a raise is sent");
        Check.Eq(FakeHost.ChatCommands.First(x => x.StartsWith("/attr", StringComparison.Ordinal)), "/attr qui 1",
            "Coordination's older cost is not used when this read left it out");

        // A reply about another stat (some other /attr sender) is not taken as our success.
        Reply(hub, "[ATTR] Strength attribute raised by 1, costing 50!");
        Check.True(TickUntil(hub, () => !raiser.Running), "a reply about another stat ends the run");
        Check.Eq(FakeHost.ChatCommands.Count(x => x.StartsWith("/attr", StringComparison.Ordinal)), 1, "and nothing more is raised");
        Check.False(FakeHost.Chat.Any(x => x.Contains("Attribute raise")), "and no raise is reported");

        // The world leaves Server Features mid-run: the next step stops.
        FakeHost.ChatCommands.Clear();
        raiser.Start(auto: false);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains("/xp all")), "second run reads costs");
        settings.FeatureServerNames = new List<string> { "SomewhereElse" };
        Reply(hub, "[XP] Your XP cost for next 1 Coordination level is: 100");
        Check.True(TickUntil(hub, () => !raiser.Running), "the run stops once the world is off");
        Check.False(FakeHost.ChatCommands.Any(x => x.StartsWith("/attr", StringComparison.Ordinal)), "no raise after the gate closed");
        settings.FeatureServerNames = new List<string>(ServerFeatureGate.DefaultServerNames);

        // Another XP spender running: neither Raise now nor +N starts.
        bool spending = true;
        raiser.HoldOff = () => spending;
        FakeHost.ChatCommands.Clear();
        raiser.Start(auto: false);
        raiser.RaiseManual("coo", 1);
        TickUntil(hub, () => false, 600);
        Check.False(raiser.Running, "held off: Raise now doesn't start");
        Check.False(FakeHost.ChatCommands.Any(), "held off: nothing is sent");
    }

    private static void GatedOff()
    {
        var settings = new LegacyUiSettings();
        var hub = NewHub("Aelrynth", settings, "attr-gated", out _);
        var raiser = hub.Progression.Attributes;
        hub.State.Character.AttrRaiseStats = new List<string> { "coo" };
        Check.NotNull(raiser.Blocker(), "a world not in the list: blocked");
        raiser.Start(auto: false);
        TickUntil(hub, () => false, 600);
        Check.False(FakeHost.ChatCommands.Any(), "nothing is sent off a feature world");

        settings.FeatureServerNames.Add("Aelrynth");
        Check.Null(raiser.Blocker(), "adding the world to Server Features unblocks it");
        settings.FeatureServerNames.Remove("Aelrynth");
        settings.ForceServerFeatures = true;
        Check.Null(raiser.Blocker(), "so does the manual override");
    }
}

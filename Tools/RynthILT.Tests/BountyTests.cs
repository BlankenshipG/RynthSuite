using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthILT.Bounty;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// ACECustom bounty tracker (2026-10-09). The server keeps the kill counts (BountyProgress is a
// server-only property), so the engine's Bounties window has RynthILT read "/bounty list" quietly
// and parse it. The line formats below are copied from ACECustom BountyManager (ShowAllBounties /
// ShowBounty / Award / NextBountyLine). The tracker runs on a fake clock; the engine's 1 s poll
// of RynthPluginGetBountiesJson is simulated by calling BuildJson.
internal static class BountyTests
{
    public static void Register(Runner r)
    {
        r.Add("bounty: /bounty list lines (counting, cooldown, QB, layers, commas)", ListLines);
        r.Add("bounty: /bounty lines, waits and chat events", HereLinesAndEvents);
        r.Add("bounty: a full read replaces the list, a partial one updates it", ApplyListRules);
        r.Add("bounty: kills and awards stamp the row; an ended cooldown shows 0 kills; state survives a relog", ChangesAndCooldown);
        r.Add("bounty: /bounty updates only the one reward it can mean", HereMatching);
        r.Add("bounty: an open window reads the list quietly, kills and awards re-read it", WindowReadsAndKills);
        r.Add("bounty: closed window sends nothing; unknown command stops reads; no reply times out", ClosedAndUnsupported);
        r.Add("bounty: the window's JSON (options, QB filter, closest first) parses in the engine", BridgeJson);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    private static void ListLines()
    {
        Check.True(BountyParse.TryParseListLine("  Lugian Mines: 1 Ascension Coin every 100 kills (at most every 5 min) - you: 34 of 100 kills.", out var a),
                   "counting line parses (leading indentation)");
        Check.Eq(a.Where, "Lugian Mines", "area");
        Check.Eq(a.Amount, 1L, "amount");
        Check.Eq(a.Item, "Ascension Coin", "item");
        Check.Eq(a.KillsRequired, 100, "kills per award");
        Check.Eq(a.CooldownSeconds, 300, "cooldown");
        Check.Eq(a.Status.Status, BountyStatus.Counting, "status");
        Check.Eq(a.Status.Kills, 34, "kills so far");

        Check.True(BountyParse.TryParseListLine("All zones: 3 Pyreal Nugget every 1,000 kills (at most every 2 min 30 sec) - you: unlocks in 1 hr 15 sec, then 1,000 kills required.", out var b),
                   "cooldown line parses");
        Check.Eq(b.Where, "All zones", "all-zones area");
        Check.Eq(b.KillsRequired, 1000, "N0 kills");
        Check.Eq(b.CooldownSeconds, 150, "2 min 30 sec");
        Check.Eq(b.Status.Status, BountyStatus.Cooldown, "cooldown status");
        Check.Eq(b.Status.WaitSeconds, 3615, "1 hr 15 sec");
        Check.Eq(b.Status.KillsRequired, 1000, "kills after the cooldown");

        Check.True(BountyParse.TryParseListLine("Valley of Death: 1,500 Trade Note - Large every 1 kill - you: needs 5,000 QB (you have 3,210).", out var c),
                   "QB line with no cooldown and a hyphenated item parses");
        Check.Eq(c.Amount, 1500L, "N0 amount");
        Check.Eq(c.Item, "Trade Note - Large", "item keeps its own ' - '");
        Check.Eq(c.KillsRequired, 1, "one kill");
        Check.Eq(c.CooldownSeconds, 0, "no cooldown");
        Check.Eq(c.Status.Status, BountyStatus.NeedsQb, "QB status");
        Check.Eq(c.Status.QbRequired, 5000L, "QB needed");
        Check.Eq(c.Status.QbHave, 3210L, "QB had");

        Check.True(BountyParse.TryParseListLine("Halls of Metos: Lower (layer 2): 2 Ascension Coin every 50 kills - you: 0 of 50 kills.", out var d),
                   "an area with ': ' and a layer parses");
        Check.Eq(d.Where, "Halls of Metos: Lower (layer 2)", "area keeps its colon and layer");

        Check.False(BountyParse.TryParseListLine("Bounties:", out _), "the header is not a reward");
        Check.False(BountyParse.TryParseListLine("Bounty: type /bounty to see the bounties where you stand.", out _), "the footer is not a reward");
        Check.False(BountyParse.TryParseListLine("Bob says, \"Lugian Mines: 1 coin every 100 kills\"", out _), "chat talk is not a reward");
        Check.True(BountyParse.IsListTerminator("Bounty: type /bounty to see the bounties where you stand."), "footer ends the reply");
        Check.True(BountyParse.IsListTerminator("Bounty: there are no bounties anywhere right now."), "'none anywhere' ends the reply");
        Check.True(BountyParse.IsNoBountiesAnywhere("Bounty: there are no bounties anywhere right now."), "'none anywhere' is recognised");
        Check.False(BountyParse.IsListTerminator("Bounty: there is no bounty here."), "/bounty's 'none here' is not the list's end");
        Check.True(BountyParse.IsUnknownCommand("Unknown command: bounty"), "ACE's unknown-command reply is recognised");
        Check.False(BountyParse.IsUnknownCommand("Unknown command: bank"), "another command's reply is not ours");
    }

    private static void HereLinesAndEvents()
    {
        Check.True(BountyParse.TryParseHereLine("Bounty: 1 Ascension Coin - 12 of 100 kills.", out var h1), "plain /bounty line");
        Check.Eq(h1.Scope, "", "no scope");
        Check.Eq(h1.Status.Kills, 12, "kills");
        Check.True(BountyParse.TryParseHereLine("Bounty (all zones): 3 Pyreal Nugget - unlocks in 45 sec, then 1 kill required.", out var h2), "all-zones /bounty line");
        Check.Eq(h2.Scope, "all zones", "all-zones scope");
        Check.Eq(h2.Status.WaitSeconds, 45, "45 sec");
        Check.True(BountyParse.TryParseHereLine("Bounty (Thaelaryn Island): 1 Ascension Coin - needs 500 QB (you have 20).", out var h3), "region /bounty line");
        Check.Eq(h3.Scope, "Thaelaryn Island", "region scope");
        Check.False(BountyParse.TryParseHereLine("Bounty: there is no bounty here.", out _), "'none here' is not a reward");
        Check.False(BountyParse.TryParseHereLine("Bounty: +1 Ascension Coin will follow in a moment.", out _), "held notice is not a reward");

        Check.Eq(BountyParse.ParseWaitSeconds("5 min"), 300, "5 min");
        Check.Eq(BountyParse.ParseWaitSeconds("2 hr 3 min 4 sec"), 7384, "hr min sec");
        Check.Eq(BountyParse.ParseWaitSeconds("soon"), 0, "unparseable wait");

        Check.True(BountyParse.TryParseEvent("Bounty unlocked: Ascension Coin - 100 kills required.", out var k1, out string item1, out int n1, out _)
                   && k1 == BountyEventKind.Unlocked && item1 == "Ascension Coin" && n1 == 100, "unlock event");
        Check.True(BountyParse.TryParseEvent("Bounty complete! +3 Pyreal Nuggets gained.", out var k2, out string item2, out _, out _)
                   && k2 == BountyEventKind.Complete && item2 == "Pyreal Nuggets", "award event");
        Check.True(BountyParse.TryParseEvent("Next bounty unlocks in 2 min 30 sec - 1 kill required.", out var k3, out _, out int n3, out int w3)
                   && k3 == BountyEventKind.NextCooldown && n3 == 1 && w3 == 150, "next-cooldown event");
        Check.True(BountyParse.TryParseEvent("Next bounty requires 1,000 kills.", out var k4, out _, out int n4, out _)
                   && k4 == BountyEventKind.NextReady && n4 == 1000, "next-ready event");
        Check.True(BountyParse.TryParseEvent("Bounty: Your inventory is full. +1 Ascension Coin gained (3 total). They're safe until you make room.", out var k5, out _, out _, out _)
                   && k5 == BountyEventKind.Held, "held event");
        Check.False(BountyParse.TryParseEvent("Bounty: there is no bounty here.", out _, out _, out _, out _), "'none here' is not an event");
        Check.False(BountyParse.TryParseEvent("Bounty: 1 Ascension Coin - 12 of 100 kills.", out _, out _, out _, out _), "a status line is not an event");
        Check.False(BountyParse.TryParseEvent("Bounty: type /bounty to see the bounties where you stand.", out _, out _, out _, out _), "the footer is not an event");
    }

    // ── Applying reads ─────────────────────────────────────────────────────────

    private static BountyListLine L(string line)
    {
        Check.True(BountyParse.TryParseListLine(line, out var l), "fixture parses: " + line);
        return l;
    }

    private static void ApplyListRules()
    {
        var t = New(out _, out _, out _);
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        t.ApplyList(new[]
        {
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 10 of 100 kills."),
            L("All zones: 3 Pyreal Nugget every 50 kills - you: 0 of 50 kills."),
        }, complete: true, noneAnywhere: false, t0);
        Check.Eq(t.State.Entries.Count, 2, "full read: two rewards");
        Check.Eq(t.State.ListedUtc, t0, "read time stamped");

        // A partial reply (no footer) only updates what it names; the other rows stay.
        t.ApplyList(new[] { L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 11 of 100 kills.") },
                    complete: false, noneAnywhere: false, t0.AddSeconds(5));
        Check.Eq(t.State.Entries.Count, 2, "partial read keeps the other row");
        Check.Eq(t.State.Entries[0].Kills, 11, "partial read updates the named row in place");

        // A full read drops areas that are gone and keeps the server's order.
        t.ApplyList(new[]
        {
            L("Thaelaryn Island: 1 Ascension Coin every 20 kills - you: 4 of 20 kills."),
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 12 of 100 kills."),
        }, complete: true, noneAnywhere: false, t0.AddSeconds(10));
        Check.Eq(string.Join(",", t.State.Entries.Select(e => e.Where)), "Thaelaryn Island,Lugian Mines", "server order, removed area gone");

        t.ApplyList(Array.Empty<BountyListLine>(), complete: true, noneAnywhere: true, t0.AddSeconds(20));
        Check.Eq(t.State.Entries.Count, 0, "'none anywhere' empties the list");
        Check.True(t.State.NoneAnywhere, "and says so");
    }

    private static void ChangesAndCooldown()
    {
        string file = Path.Combine(Program.TempRoot, "bounty-changes", BountyTracker.FileName);
        var t = New(out _, out _, out _, file);
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        const string Head = "Lugian Mines: 1 Ascension Coin every 3 kills (at most every 5 min) - you: ";
        t.ApplyList(new[] { L(Head + "1 of 3 kills.") }, true, false, t0);
        Check.Eq(t.State.Entries[0].ChangedUtc, default(DateTime), "first sighting is not a change");

        t.ApplyList(new[] { L(Head + "2 of 3 kills.") }, true, false, t0.AddSeconds(10));
        Check.Eq(t.State.Entries[0].ChangedUtc, t0.AddSeconds(10), "a new kill stamps the row");

        t.ApplyList(new[] { L(Head + "unlocks in 5 min, then 3 kills required.") }, true, false, t0.AddSeconds(20));
        var e = t.State.Entries[0];
        Check.Eq(e.ChangedUtc, t0.AddSeconds(20), "the award stamps the row");
        Check.Eq(e.UnlockAtUtc, t0.AddSeconds(320), "unlock time = read time + wait");
        Check.Eq(BountyTracker.Effective(e, t0.AddSeconds(100)).Status, BountyStatus.Cooldown, "still cooling down");
        var after = BountyTracker.Effective(e, t0.AddSeconds(321));
        Check.True(after.Status == BountyStatus.Counting && after.Kills == 0, "after the cooldown: 0 of N kills");
        Check.True(BountyTracker.Describe(e, t0.AddSeconds(100)).Contains("unlocks in 3 min 40 sec"), "chat text counts the cooldown down");

        // The state survives a relog (the window fills right after login).
        t.Command("set hideqblocked 1");
        t.Save();
        var loaded = New(out _, out _, out _, file);
        Check.Eq(loaded.State.Entries.Count, 1, "entries saved per character");
        Check.Eq(loaded.State.Entries[0].UnlockAtUtc, t0.AddSeconds(320), "unlock time saved");
        Check.True(loaded.State.HideQbLocked, "options saved");
    }

    private static void HereMatching()
    {
        var t = New(out _, out _, out _);
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        t.ApplyList(new[]
        {
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 10 of 100 kills."),
            L("Halls of Metos: 1 Ascension Coin every 100 kills - you: 50 of 100 kills."),
            L("Thaelaryn Island: 2 Ascension Coin every 20 kills - you: 4 of 20 kills."),
            L("All zones: 1 Ascension Coin every 100 kills - you: 0 of 100 kills."),
        }, true, false, t0);

        Check.True(BountyParse.TryParseHereLine("Bounty (Thaelaryn Island): 2 Ascension Coin - 5 of 20 kills.", out var region), "fixture");
        Check.True(t.ApplyHere(region, t0.AddSeconds(5)), "a region line names its area");
        Check.Eq(t.State.Entries[2].Kills, 5, "region row updated");

        Check.True(BountyParse.TryParseHereLine("Bounty (all zones): 1 Ascension Coin - 1 of 100 kills.", out var zones), "fixture");
        Check.True(t.ApplyHere(zones, t0.AddSeconds(6)), "an all-zones line names its area");
        Check.Eq(t.State.Entries[3].Kills, 1, "all-zones row updated");

        Check.True(BountyParse.TryParseHereLine("Bounty: 1 Ascension Coin - 11 of 100 kills.", out var plain), "fixture");
        Check.False(t.ApplyHere(plain, t0.AddSeconds(7)), "two dungeons pay the same reward: left alone");
        Check.Eq(t.State.Entries[0].Kills, 10, "first dungeon untouched");
        Check.Eq(t.State.Entries[1].Kills, 50, "second dungeon untouched");
    }

    // ── The read loop ──────────────────────────────────────────────────────────

    private const string ListReplyCounting = "  Lugian Mines: 1 Ascension Coin every 100 kills - you: 34 of 100 kills.";
    private const string Footer = "Bounty: type /bounty to see the bounties where you stand.";

    private static void WindowReadsAndKills()
    {
        var t = New(out var clock, out var sent, out _);
        t.Command("set minrefreshseconds 5");
        Run(t, clock, 500, windowOpen: true);
        Check.Eq(Reads(sent), 1, "opening the window reads /bounty list");
        Check.True(t.Reading, "a read is in progress");

        Check.True(t.OnChat("Bounties:"), "the header is hidden from chat");
        Check.True(t.OnChat(ListReplyCounting), "reward lines are hidden from chat");
        Check.True(t.OnChat(Footer), "the footer is hidden and ends the read");
        Check.False(t.Reading, "the read is over");
        Check.Eq(t.State.Entries.Count, 1, "the read fills the list");
        Check.Eq(t.State.Entries[0].Kills, 34, "with the player's kills");

        // A kill re-reads after the debounce, but not sooner than MinRefreshSeconds after the last read.
        t.OnKill();
        Run(t, clock, 3000, windowOpen: true);
        Check.Eq(Reads(sent), 1, "no re-read inside MinRefreshSeconds");
        Run(t, clock, 3000, windowOpen: true);
        Check.Eq(Reads(sent), 2, "a kill re-reads the list");
        t.OnChat("Bounties:");
        t.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills - you: 35 of 100 kills.");
        t.OnChat(Footer);
        Check.Eq(t.State.Entries[0].Kills, 35, "the kill shows up");
        Check.True(t.State.Entries[0].ChangedUtc != default, "and the row is highlighted");

        // Award lines are shown to the player and also trigger a read, once MinRefreshSeconds
        // have passed since the last one (review 2026-10-09: awards used to skip the interval).
        Check.False(t.OnChat("Bounty complete! +1 Ascension Coin gained."), "award lines stay in chat");
        Check.Eq(t.State.LastEvent, "Bounty complete! +1 Ascension Coin gained.", "the window shows the last event");
        Run(t, clock, 2000, windowOpen: true);
        Check.Eq(Reads(sent), 2, "the award's re-read waits for MinRefreshSeconds");
        Run(t, clock, 3000, windowOpen: true);
        Check.Eq(Reads(sent), 3, "an award re-reads the list");

        // A reply whose footer never comes ends after a quiet moment, as a partial update.
        t.OnChat("Bounties:");
        t.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills (at most every 5 min) - you: unlocks in 5 min, then 100 kills required.");
        Run(t, clock, 2000, windowOpen: true);
        Check.False(t.Reading, "a reply without its footer still ends");
        Check.Eq(t.State.Entries[0].Status, (int)BountyStatus.Cooldown, "and is applied");

        // Lines the player asks for themselves are not hidden but still update the list.
        Check.False(t.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills - you: 0 of 100 kills."), "a typed /bounty list line stays in chat");
        Check.Eq(t.State.Entries[0].Status, (int)BountyStatus.Counting, "and updates the row");

        // Closing the window (the engine stops polling; 3 s grace) stops the reads.
        Run(t, clock, 5000, windowOpen: false);
        int before = Reads(sent);
        t.OnKill();
        Run(t, clock, 20000, windowOpen: false);
        Check.Eq(Reads(sent), before, "no reads once the window is closed");
    }

    private static void ClosedAndUnsupported()
    {
        var t = New(out var clock, out var sent, out var chat);
        t.Command("set minrefreshseconds 5");
        t.OnKill();
        Run(t, clock, 10000, windowOpen: false);
        Check.Eq(Reads(sent), 0, "closed window: kills send nothing");

        Run(t, clock, 500, windowOpen: true);
        Check.Eq(Reads(sent), 1, "opening the window reads the list");
        Check.True(t.OnChat("Unknown command: bounty"), "the unknown-command reply to our read is hidden");
        Check.True(t.Unsupported, "'Unknown command: bounty' marks the server unsupported");

        t.OnKill();
        Run(t, clock, 10 * 60_000, windowOpen: true);
        Check.Eq(Reads(sent), 1, "no automatic reads on an unsupported server, periodic included");

        t.HandleChatCommand(new[] { "/ilt", "bountys", "refresh" });
        Check.False(t.Unsupported, "a manual refresh tries again");
        Run(t, clock, 500, windowOpen: true);
        Check.Eq(Reads(sent), 2, "and reads the list");
        Check.True(chat.Any(c => c.Contains("Reading /bounty list")), "the command answers in chat");

        Run(t, clock, 11000, windowOpen: true);
        Check.False(t.Reading, "no reply: the read is abandoned");
        Check.True(t.BuildJson().Contains("no reply to /bounty list"), "and the window says so");

        // With the window closed a manual refresh sends nothing (review 2026-10-09: reads only
        // while the window is open); the next open reads.
        Run(t, clock, 5000, windowOpen: false);
        Check.True(t.Command("refresh").Contains("window is open"), "a closed-window refresh says why it waits");
        Check.Eq(Reads(sent), 2, "a closed-window refresh sends nothing");
        Run(t, clock, 500, windowOpen: true);
        Check.Eq(Reads(sent), 3, "the next open reads");

        chat.Clear();
        t.HandleChatCommand(new[] { "/ilt", "bounty" });
        Check.True(chat.Any(c => c.Contains("/rc bounties")), "'/ilt bounty' points at the window");
    }

    private static void BridgeJson()
    {
        var t = New(out _, out _, out _);
        var now = DateTime.UtcNow;
        t.ApplyList(new[]
        {
            L("Valley of Death: 1,500 Trade Note - Large every 1 kill - you: needs 5,000 QB (you have 3,210)."),
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 10 of 100 kills."),
            L("Thaelaryn Island: 2 Ascension Coin every 20 kills (at most every 5 min) - you: unlocks in 2 min, then 20 kills required."),
            L("Halls of Metos: 1 Ascension Coin every 10 kills - you: 9 of 10 kills."),
        }, true, false, now);

        Check.Eq(t.Command("set minrefreshseconds 1"), "", "option accepted");
        Check.Eq(t.State.MinRefreshSeconds, BountyTracker.MinRefreshFloorSeconds, "MinRefreshSeconds is clamped to the floor");
        t.Command("set periodicminutes 999");
        Check.Eq(t.State.PeriodicRefreshMinutes, BountyTracker.PeriodicCeilingMinutes, "PeriodicRefreshMinutes is clamped");
        Check.True(t.Command("set nonsense 1").Contains("unknown"), "unknown option is reported");

        using (var doc = JsonDocument.Parse(t.BuildJson()))
        {
            var root = doc.RootElement;
            Check.Eq(root.GetProperty("rows").GetArrayLength(), 4, "all rows by default");
            Check.Eq(root.GetProperty("rows")[0].GetProperty("where").GetString(), "Valley of Death", "server order by default");
            Check.Eq(root.GetProperty("minRefreshSeconds").GetInt32(), 5, "camelCase option names");
        }

        t.Command("set hideqblocked 1");
        t.Command("set sortbyprogress on");
        string json = t.BuildJson();
        using (var doc = JsonDocument.Parse(json))
        {
            var rows = doc.RootElement.GetProperty("rows");
            Check.Eq(rows.GetArrayLength(), 3, "QB-locked row hidden");
            Check.Eq(doc.RootElement.GetProperty("hiddenCount").GetInt32(), 1, "and counted");
            Check.Eq(rows[0].GetProperty("where").GetString(), "Halls of Metos", "closest first: 9 of 10");
            Check.Eq(rows[2].GetProperty("where").GetString(), "Thaelaryn Island", "cooldowns after counting rows");
        }

#if ENGINE_BOUNTIES
        var snap = RynthCore.Engine.UI.Data.BountiesSnapshot.Parse(json);
        Check.NotNull(snap, "the engine parses the plugin's JSON");
        Check.True(snap!.HideQbLocked && snap.SortByProgress, "engine sees the options");
        Check.Eq(snap.MinRefreshSeconds, 5, "engine sees MinRefreshSeconds");
        Check.Eq(snap.HiddenCount, 1, "engine sees the hidden count");
        Check.Eq(snap.Rows.Count, 3, "engine sees the rows");
        var cd = snap.Rows.Single(r => r.Where == "Thaelaryn Island");
        Check.Eq(cd.Status, (int)RynthCore.Engine.UI.Data.BountyRowStatus.Cooldown, "engine status enum matches the plugin's");
        Check.True(cd.UnlockInSec > 100 && cd.UnlockInSec <= 120, $"cooldown seconds left ({cd.UnlockInSec})");
        Check.Eq(cd.CooldownSeconds, 300, "cooldown length");
        var halls = snap.Rows[0];
        Check.True(halls.Kills == 9 && halls.KillsRequired == 10 && halls.Amount == 1 && halls.Item == "Ascension Coin", "row fields");
#endif
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class Clock { public long Ms = 1_000_000; }

    private static BountyTracker New(out Clock clock, out List<string> sent, out List<string> chat, string file = "")
    {
        var c = new Clock();
        var s = new List<string>();
        var ch = new List<string>();
        if (file.Length > 0) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var t = new BountyTracker(s.Add, ch.Add, _ => { }, file, () => c.Ms);
        clock = c;
        sent = s;
        chat = ch;
        return t;
    }

    private static int Reads(List<string> sent) => sent.Count(x => x == BountyTracker.ListCommand);

    /// <summary>Advances the clock in 250 ms steps, ticking; with the window open the engine polls every second.</summary>
    private static void Run(BountyTracker t, Clock clock, long ms, bool windowOpen)
    {
        for (long elapsed = 0; elapsed < ms; elapsed += 250)
        {
            clock.Ms += 250;
            if (windowOpen && elapsed % 1000 == 0) t.BuildJson();
            t.Tick();
        }
    }
}

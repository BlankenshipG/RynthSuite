using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// ACECustom bounty tracker (2026-10-08). The server keeps the kill counts (BountyProgress is a
// server-only property), so the Bounties window reads "/bounty list" quietly and parses it. The
// line formats below are copied from ACECustom BountyManager (ShowAllBounties / ShowBounty /
// Award / NextBountyLine).
internal static class BountyTests
{
    private const uint Player = 0x50000C02;

    public static void Register(Runner r)
    {
        r.Add("bounty: /bounty list lines (counting, cooldown, QB, layers, commas)", ListLines);
        r.Add("bounty: /bounty lines, waits and chat events", HereLinesAndEvents);
        r.Add("bounty: a full read replaces the list, a partial one updates it", ApplyListRules);
        r.Add("bounty: kills and awards stamp the row; an ended cooldown shows 0 kills", ChangesAndCooldown);
        r.Add("bounty: /bounty updates only the one reward it can mean", HereMatching);
        r.Add("bounty: window open reads the list quietly, kills re-read it", WindowReadsAndKills);
        r.Add("bounty: closed window sends nothing; unknown command stops reads", ClosedAndUnsupported);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    private static void ListLines()
    {
        Check.True(IltBountyParse.TryParseListLine("  Lugian Mines: 1 Ascension Coin every 100 kills (at most every 5 min) - you: 34 of 100 kills.", out var a),
                   "counting line parses (leading indentation)");
        Check.Eq(a.Where, "Lugian Mines", "area");
        Check.Eq(a.Amount, 1L, "amount");
        Check.Eq(a.Item, "Ascension Coin", "item");
        Check.Eq(a.KillsRequired, 100, "kills per award");
        Check.Eq(a.CooldownSeconds, 300, "cooldown");
        Check.Eq(a.Status.Status, IltBountyStatus.Counting, "status");
        Check.Eq(a.Status.Kills, 34, "kills so far");

        Check.True(IltBountyParse.TryParseListLine("All zones: 3 Pyreal Nugget every 1,000 kills (at most every 2 min 30 sec) - you: unlocks in 1 hr 15 sec, then 1,000 kills required.", out var b),
                   "cooldown line parses");
        Check.Eq(b.Where, "All zones", "all-zones area");
        Check.Eq(b.KillsRequired, 1000, "N0 kills");
        Check.Eq(b.CooldownSeconds, 150, "2 min 30 sec");
        Check.Eq(b.Status.Status, IltBountyStatus.Cooldown, "cooldown status");
        Check.Eq(b.Status.WaitSeconds, 3615, "1 hr 15 sec");
        Check.Eq(b.Status.KillsRequired, 1000, "kills after the cooldown");

        Check.True(IltBountyParse.TryParseListLine("Valley of Death: 1,500 Trade Note - Large every 1 kill - you: needs 5,000 QB (you have 3,210).", out var c),
                   "QB line with no cooldown and a hyphenated item parses");
        Check.Eq(c.Amount, 1500L, "N0 amount");
        Check.Eq(c.Item, "Trade Note - Large", "item keeps its own ' - '");
        Check.Eq(c.KillsRequired, 1, "one kill");
        Check.Eq(c.CooldownSeconds, 0, "no cooldown");
        Check.Eq(c.Status.Status, IltBountyStatus.NeedsQb, "QB status");
        Check.Eq(c.Status.QbRequired, 5000L, "QB needed");
        Check.Eq(c.Status.QbHave, 3210L, "QB had");

        Check.True(IltBountyParse.TryParseListLine("Halls of Metos: Lower (layer 2): 2 Ascension Coin every 50 kills - you: 0 of 50 kills.", out var d),
                   "an area with ': ' and a layer parses");
        Check.Eq(d.Where, "Halls of Metos: Lower (layer 2)", "area keeps its colon and layer");

        Check.False(IltBountyParse.TryParseListLine("Bounties:", out _), "the header is not a reward");
        Check.False(IltBountyParse.TryParseListLine("Bounty: type /bounty to see the bounties where you stand.", out _), "the footer is not a reward");
        Check.False(IltBountyParse.TryParseListLine("Bob says, \"Lugian Mines: 1 coin every 100 kills\"", out _), "chat talk is not a reward");
        Check.True(IltBountyParse.IsListTerminator("Bounty: type /bounty to see the bounties where you stand."), "footer ends the reply");
        Check.True(IltBountyParse.IsListTerminator("Bounty: there are no bounties anywhere right now."), "'none anywhere' ends the reply");
        Check.True(IltBountyParse.IsNoBountiesAnywhere("Bounty: there are no bounties anywhere right now."), "'none anywhere' is recognised");
        Check.False(IltBountyParse.IsListTerminator("Bounty: there is no bounty here."), "/bounty's 'none here' is not the list's end");
    }

    private static void HereLinesAndEvents()
    {
        Check.True(IltBountyParse.TryParseHereLine("Bounty: 1 Ascension Coin - 12 of 100 kills.", out var h1), "plain /bounty line");
        Check.Eq(h1.Scope, "", "no scope");
        Check.Eq(h1.Status.Kills, 12, "kills");
        Check.True(IltBountyParse.TryParseHereLine("Bounty (all zones): 3 Pyreal Nugget - unlocks in 45 sec, then 1 kill required.", out var h2), "all-zones /bounty line");
        Check.Eq(h2.Scope, "all zones", "all-zones scope");
        Check.Eq(h2.Status.WaitSeconds, 45, "45 sec");
        Check.True(IltBountyParse.TryParseHereLine("Bounty (Thaelaryn Island): 1 Ascension Coin - needs 500 QB (you have 20).", out var h3), "region /bounty line");
        Check.Eq(h3.Scope, "Thaelaryn Island", "region scope");
        Check.False(IltBountyParse.TryParseHereLine("Bounty: there is no bounty here.", out _), "'none here' is not a reward");
        Check.False(IltBountyParse.TryParseHereLine("Bounty: +1 Ascension Coin will follow in a moment.", out _), "held notice is not a reward");

        Check.Eq(IltBountyParse.ParseWaitSeconds("5 min"), 300, "5 min");
        Check.Eq(IltBountyParse.ParseWaitSeconds("2 hr 3 min 4 sec"), 7384, "hr min sec");
        Check.Eq(IltBountyParse.ParseWaitSeconds("soon"), 0, "unparseable wait");

        Check.True(IltBountyParse.TryParseEvent("Bounty unlocked: Ascension Coin - 100 kills required.", out var k1, out string item1, out int n1, out _)
                   && k1 == IltBountyEventKind.Unlocked && item1 == "Ascension Coin" && n1 == 100, "unlock event");
        Check.True(IltBountyParse.TryParseEvent("Bounty complete! +3 Pyreal Nuggets gained.", out var k2, out string item2, out _, out _)
                   && k2 == IltBountyEventKind.Complete && item2 == "Pyreal Nuggets", "award event");
        Check.True(IltBountyParse.TryParseEvent("Next bounty unlocks in 2 min 30 sec - 1 kill required.", out var k3, out _, out int n3, out int w3)
                   && k3 == IltBountyEventKind.NextCooldown && n3 == 1 && w3 == 150, "next-cooldown event");
        Check.True(IltBountyParse.TryParseEvent("Next bounty requires 1,000 kills.", out var k4, out _, out int n4, out _)
                   && k4 == IltBountyEventKind.NextReady && n4 == 1000, "next-ready event");
        Check.True(IltBountyParse.TryParseEvent("Bounty: Your inventory is full. +1 Ascension Coin gained (3 total). They're safe until you make room.", out var k5, out _, out _, out _)
                   && k5 == IltBountyEventKind.Held, "held event");
        Check.False(IltBountyParse.TryParseEvent("Bounty: there is no bounty here.", out _, out _, out _, out _), "'none here' is not an event");
        Check.False(IltBountyParse.TryParseEvent("Bounty: 1 Ascension Coin - 12 of 100 kills.", out _, out _, out _, out _), "a status line is not an event");
        Check.False(IltBountyParse.TryParseEvent("Bounty: type /bounty to see the bounties where you stand.", out _, out _, out _, out _), "the footer is not an event");
    }

    // ── Applying reads ─────────────────────────────────────────────────────────

    private static IltBountyListLine L(string line)
    {
        Check.True(IltBountyParse.TryParseListLine(line, out var l), "fixture parses: " + line);
        return l;
    }

    private static void ApplyListRules()
    {
        var hub = NewHub("bounty-apply");
        var b = hub.Bounties;
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        b.ApplyList(new[]
        {
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 10 of 100 kills."),
            L("All zones: 3 Pyreal Nugget every 50 kills - you: 0 of 50 kills."),
        }, complete: true, noneAnywhere: false, t0);
        Check.Eq(b.Snapshot.Count, 2, "full read: two rewards");
        Check.Eq(hub.State.Bounty.ListedUtc, t0, "read time stamped");

        // A partial reply (no footer) only updates what it names; the other rows stay.
        b.ApplyList(new[] { L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 11 of 100 kills.") },
                    complete: false, noneAnywhere: false, t0.AddSeconds(5));
        Check.Eq(b.Snapshot.Count, 2, "partial read keeps the other row");
        Check.Eq(b.Snapshot[0].Kills, 11, "partial read updates the named row in place");

        // A full read drops areas that are gone and keeps the server's order.
        b.ApplyList(new[]
        {
            L("Thaelaryn Island: 1 Ascension Coin every 20 kills - you: 4 of 20 kills."),
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 12 of 100 kills."),
        }, complete: true, noneAnywhere: false, t0.AddSeconds(10));
        Check.Eq(string.Join(",", b.Snapshot.Select(e => e.Where)), "Thaelaryn Island,Lugian Mines", "server order, removed area gone");

        b.ApplyList(Array.Empty<IltBountyListLine>(), complete: true, noneAnywhere: true, t0.AddSeconds(20));
        Check.Eq(b.Snapshot.Count, 0, "'none anywhere' empties the list");
        Check.True(hub.State.Bounty.NoneAnywhere, "and says so");
    }

    private static void ChangesAndCooldown()
    {
        var hub = NewHub("bounty-changes");
        var b = hub.Bounties;
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        const string Head = "Lugian Mines: 1 Ascension Coin every 3 kills (at most every 5 min) - you: ";
        b.ApplyList(new[] { L(Head + "1 of 3 kills.") }, true, false, t0);
        Check.Eq(b.Snapshot[0].ChangedUtc, default(DateTime), "first sighting is not a change");

        b.ApplyList(new[] { L(Head + "2 of 3 kills.") }, true, false, t0.AddSeconds(10));
        Check.Eq(b.Snapshot[0].ChangedUtc, t0.AddSeconds(10), "a new kill stamps the row");

        b.ApplyList(new[] { L(Head + "unlocks in 5 min, then 3 kills required.") }, true, false, t0.AddSeconds(20));
        var e = b.Snapshot[0];
        Check.Eq(e.ChangedUtc, t0.AddSeconds(20), "the award stamps the row");
        Check.Eq(e.UnlockAtUtc, t0.AddSeconds(320), "unlock time = read time + wait");
        Check.Eq(IltBounties.Effective(e, t0.AddSeconds(100)).Status, IltBountyStatus.Cooldown, "still cooling down");
        var after = IltBounties.Effective(e, t0.AddSeconds(321));
        Check.True(after.Status == IltBountyStatus.Counting && after.Kills == 0, "after the cooldown: 0 of N kills");
        Check.True(IltBounties.Describe(e, t0.AddSeconds(100)).Contains("unlocks in 3m 40s"), "chat text counts the cooldown down");

        // The state survives a save / load (the window fills right after login).
        hub.OnLogout();
        var store = new IltHubStore(FakeHost.Create(Player), Path.Combine(Program.TempRoot, "bounty-changes"));
        var loaded = store.LoadState();
        Check.Eq(loaded.Bounty.Entries.Count, 1, "entries saved with the Hub state");
        Check.Eq(loaded.Bounty.Entries[0].UnlockAtUtc, t0.AddSeconds(320), "unlock time saved");
    }

    private static void HereMatching()
    {
        var hub = NewHub("bounty-here");
        var b = hub.Bounties;
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        b.ApplyList(new[]
        {
            L("Lugian Mines: 1 Ascension Coin every 100 kills - you: 10 of 100 kills."),
            L("Halls of Metos: 1 Ascension Coin every 100 kills - you: 50 of 100 kills."),
            L("Thaelaryn Island: 2 Ascension Coin every 20 kills - you: 4 of 20 kills."),
            L("All zones: 1 Ascension Coin every 100 kills - you: 0 of 100 kills."),
        }, true, false, t0);

        Check.True(IltBountyParse.TryParseHereLine("Bounty (Thaelaryn Island): 2 Ascension Coin - 5 of 20 kills.", out var region), "fixture");
        Check.True(b.ApplyHere(region, t0.AddSeconds(5)), "a region line names its area");
        Check.Eq(b.Snapshot[2].Kills, 5, "region row updated");

        Check.True(IltBountyParse.TryParseHereLine("Bounty (all zones): 1 Ascension Coin - 1 of 100 kills.", out var zones), "fixture");
        Check.True(b.ApplyHere(zones, t0.AddSeconds(6)), "an all-zones line names its area");
        Check.Eq(b.Snapshot[3].Kills, 1, "all-zones row updated");

        Check.True(IltBountyParse.TryParseHereLine("Bounty: 1 Ascension Coin - 11 of 100 kills.", out var plain), "fixture");
        Check.False(b.ApplyHere(plain, t0.AddSeconds(7)), "two dungeons pay the same reward: left alone");
        Check.Eq(b.Snapshot[0].Kills, 10, "first dungeon untouched");
        Check.Eq(b.Snapshot[1].Kills, 50, "second dungeon untouched");
    }

    // ── The loop on the fake host ──────────────────────────────────────────────

    private const string ListReplyCounting = "  Lugian Mines: 1 Ascension Coin every 100 kills - you: 34 of 100 kills.";
    private const string Footer = "Bounty: type /bounty to see the bounties where you stand.";

    private static void WindowReadsAndKills()
    {
        var hub = NewHub("bounty-window");
        hub.State.Bounty.MinRefreshSeconds = 0;   // below the UI floor so the test needn't wait 5 s
        hub.SetSectionOpen(IltSection.Bounties, true);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains(IltBounties.ListCommand)),
                   $"opening the window reads /bounty list (sent: {string.Join(", ", FakeHost.ChatCommands)}; ilt={hub.Options.IsIltLikeWorld} available={hub.Available})");

        Check.True(hub.OnChat("Bounties:"), "the header is hidden from chat");
        Check.True(hub.OnChat(ListReplyCounting), "reward lines are hidden from chat");
        Check.True(hub.OnChat(Footer), "the footer is hidden and ends the read");
        hub.Tick();
        Check.Eq(hub.Bounties.Snapshot.Count, 1, "the read fills the window");
        Check.Eq(hub.Bounties.Snapshot[0].Kills, 34, "with the player's kills");

        // A kill re-reads after the short debounce, and the new count lands on the row.
        hub.RecordKill();
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Count(x => x == IltBounties.ListCommand) == 2), "a kill re-reads the list");
        hub.OnChat("Bounties:");
        hub.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills - you: 35 of 100 kills.");
        hub.OnChat(Footer);
        hub.Tick();
        Check.Eq(hub.Bounties.Snapshot[0].Kills, 35, "the kill shows up");
        Check.True(hub.Bounties.Snapshot[0].ChangedUtc != default, "and the row is highlighted");

        // Award lines are shown to the player and also trigger a read.
        Check.False(hub.OnChat("Bounty complete! +1 Ascension Coin gained."), "award lines stay in chat");
        Check.Eq(hub.State.Bounty.LastEvent, "Bounty complete! +1 Ascension Coin gained.", "the window shows the last event");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Count(x => x == IltBounties.ListCommand) == 3), "an award re-reads the list");

        // Lines the player asks for themselves are not hidden but still update the window.
        hub.OnChat("Bounties:");
        hub.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills (at most every 5 min) - you: unlocks in 5 min, then 100 kills required.");
        hub.OnChat(Footer);
        hub.Tick();
        Check.False(hub.OnChat("  Lugian Mines: 1 Ascension Coin every 100 kills - you: 0 of 100 kills."), "a typed /bounty list line stays in chat");
    }

    private static void ClosedAndUnsupported()
    {
        var hub = NewHub("bounty-closed");
        hub.State.Bounty.MinRefreshSeconds = 0;
        hub.RecordKill();
        TickUntil(hub, () => false, 2500);
        Check.False(FakeHost.ChatCommands.Contains(IltBounties.ListCommand), "closed window: kills send nothing");

        hub.SetSectionOpen(IltSection.Bounties, true);
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Contains(IltBounties.ListCommand)), "opening after kills reads the list");
        hub.OnChat("Unknown command: bounty");
        hub.Tick();
        Check.True(hub.Bounties.Unsupported, "'Unknown command: bounty' marks the server unsupported");

        int sent = FakeHost.ChatCommands.Count(x => x == IltBounties.ListCommand);
        hub.RecordKill();
        TickUntil(hub, () => false, 2500);
        Check.Eq(FakeHost.ChatCommands.Count(x => x == IltBounties.ListCommand), sent, "no automatic reads on an unsupported server");

        Check.True(hub.HandleCommand("bountys", new[] { "refresh" }), "'/ra bountys refresh' is handled");
        Check.False(hub.Bounties.Unsupported, "a manual refresh tries again");
        Check.True(TickUntil(hub, () => FakeHost.ChatCommands.Count(x => x == IltBounties.ListCommand) == sent + 1), "and reads the list");

        Check.True(hub.HandleCommand("bounty", new[] { "hide" }), "'/ra bounty hide' is handled");
        Check.False(hub.IsSectionOpen(IltSection.Bounties), "and closes the window");
        Check.True(IltSections.TryParse("bountys", out var s) && s == IltSection.Bounties, "'/ra hub open bountys' names the window");
    }

    private static IltHubController NewHub(string folder)
    {
        FakeHost.Reset();
        FakeHost.WorldName = "InfiniteLeaftide";
        var host = FakeHost.Create(Player);
        string dir = Path.Combine(Program.TempRoot, folder);
        Directory.CreateDirectory(dir);
        var settings = new LegacyUiSettings();
        var hub = new IltHubController(host, dir, () => null, () => settings, () => null, () => { });
        // The server reported at least one Hub feature (as after the login probe). A passive bit,
        // so no other feature (e.g. the bank auto-refresh) queues a command ahead of /bounty list.
        hub.Options.Set(IltFeature.PetBond, IltTri.On);
        return hub;
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
}

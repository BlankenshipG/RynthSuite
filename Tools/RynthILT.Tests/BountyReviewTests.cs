using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthILT.Bounty;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The RynthILT review of SilentKelpie's tracker (2026-10-09). Two promises, checked here on a
// fake clock:
//   * rate: at most one "/bounty list" per MinRefreshSeconds (5 s floor, whatever asks: kills,
//     award lines every kill, Refresh spam, opening and closing the window), and none while the
//     window is closed;
//   * hidden chat: only the reply to the tracker's own read is eaten. A /bounty the player types
//     is shown, also when it overlaps a read of ours, and our own command coming back through the
//     engine's chat bar dispatcher is not mistaken for the player's.
internal static class BountyReviewTests
{
    public static void Register(Runner r)
    {
        r.Add("review: awards on every kill never read more than once per MinRefreshSeconds", AwardsEveryKill);
        r.Add("review: Refresh spam and window open/close keep the 5 s floor", RefreshSpamAndToggling);
        r.Add("review: a long mixed session: every read 5 s apart and only while the window is open", MixedSession);
        r.Add("review: a /bounty list the player types is shown and stands in for the next read", TypedListShown);
        r.Add("review: a /bounty the player types during our read: the rest of that read is shown", TypedDuringCapture);
        r.Add("review: our own /bounty list coming back through the chat bar is not the player's", OwnEcho);
        r.Add("review: command matching and long lines", Matching);
    }

    private const string Header = "Bounties:";
    private const string Line = "  Lugian Mines: 1 Ascension Coin every 1 kill - you: 0 of 1 kills.";
    private const string Footer = "Bounty: type /bounty to see the bounties where you stand.";

    private sealed class Rig
    {
        public long Ms = 1_000_000;
        public readonly List<long> SentAt = new();
        public readonly List<bool> SentWhileOpen = new();
        public BountyTracker T = null!;
        public bool Open;
        private long _lastPollMs;

        public void Step(long ms)
        {
            for (long e = 0; e < ms; e += 250)
            {
                Ms += 250;
                if (Open && Ms - _lastPollMs >= 1000) { T.BuildJson(); _lastPollMs = Ms; }
                T.Tick();
            }
        }

        /// <summary>The server answers our read at once (header, a line, footer).</summary>
        public void Reply()
        {
            T.OnChat(Header);
            T.OnChat(Line);
            T.OnChat(Footer);
        }
    }

    private static Rig New(int minRefreshSeconds = 5)
    {
        var rig = new Rig();
        rig.T = new BountyTracker(cmd =>
        {
            if (cmd != BountyTracker.ListCommand) return;
            rig.SentAt.Add(rig.Ms);
            rig.SentWhileOpen.Add(rig.T.WindowOpen);
        }, _ => { }, _ => { }, string.Empty, () => rig.Ms);
        rig.T.Command($"set minrefreshseconds {minRefreshSeconds}");
        return rig;
    }

    private static long MinGap(List<long> at)
    {
        long min = long.MaxValue;
        for (int i = 1; i < at.Count; i++) min = Math.Min(min, at[i] - at[i - 1]);
        return min;
    }

    private static void AwardsEveryKill()
    {
        var rig = New(minRefreshSeconds: 10);
        rig.Open = true;
        // A 1-kill bounty with no cooldown: every kill prints an award, for two minutes.
        for (int s = 0; s < 120; s++)
        {
            rig.T.OnKill();
            rig.T.OnChat("Bounty complete! +1 Ascension Coin gained.");
            rig.T.OnChat("Next bounty requires 1 kill.");
            rig.Step(1000);
            if (rig.T.Reading) rig.Reply();
        }
        Check.True(rig.SentAt.Count >= 2, $"it still reads ({rig.SentAt.Count} reads)");
        Check.True(rig.SentAt.Count <= 13, $"at most one read per 10 s over 120 s ({rig.SentAt.Count} reads)");
        Check.True(MinGap(rig.SentAt) >= 10_000, $"reads at least MinRefreshSeconds apart (min gap {MinGap(rig.SentAt)} ms)");
    }

    private static void RefreshSpamAndToggling()
    {
        var rig = New(minRefreshSeconds: 30);
        rig.Open = true;
        rig.Step(500);
        rig.Reply();
        Check.Eq(rig.SentAt.Count, 1, "opening reads once");

        // Refresh clicked every half second for 20 s: manual reads keep the 5 s floor.
        for (int i = 0; i < 40; i++)
        {
            rig.T.Command("refresh");
            rig.Step(500);
            if (rig.T.Reading) rig.Reply();
        }
        Check.True(rig.SentAt.Count <= 5, $"20 s of Refresh spam: at most 4 more reads ({rig.SentAt.Count} in all)");
        Check.True(MinGap(rig.SentAt) >= 5_000, $"manual reads at least 5 s apart (min gap {MinGap(rig.SentAt)} ms)");

        // Kills while closed, then the window toggled open and shut every 4 s for a minute.
        int before = rig.SentAt.Count;
        for (int i = 0; i < 15; i++)
        {
            rig.Open = false;
            rig.T.OnKill();
            rig.Step(4000);
            rig.Open = true;
            rig.Step(1000);
            if (rig.T.Reading) rig.Reply();
        }
        Check.True(rig.SentAt.Count - before <= 3, $"open/close every 5 s for 75 s with MinRefreshSeconds 30: at most 3 reads ({rig.SentAt.Count - before})");
        Check.True(MinGap(rig.SentAt) >= 5_000, "and never two within 5 s");
        Check.True(rig.SentWhileOpen.All(o => o), "every read went out with the window open");
    }

    private static void MixedSession()
    {
        var rig = New(minRefreshSeconds: 5);
        var rnd = new Random(20261009);
        for (int s = 0; s < 1800; s++)   // 30 minutes
        {
            if (s % 120 == 0) rig.Open = !rig.Open;
            int roll = rnd.Next(100);
            if (roll < 30) rig.T.OnKill();
            else if (roll < 35) rig.T.OnChat("Bounty complete! +1 Ascension Coin gained.");
            else if (roll < 37) rig.T.Command("refresh");
            else if (roll < 38) rig.T.OnChat("Bounty unlocked: Ascension Coin - 1 kill required.");
            rig.Step(1000);
            if (rig.T.Reading && rnd.Next(4) != 0) rig.Reply();   // now and then the server says nothing
        }
        Check.True(rig.SentAt.Count > 10, $"reads happened ({rig.SentAt.Count})");
        Check.True(MinGap(rig.SentAt) >= 5_000, $"every read at least 5 s after the last (min gap {MinGap(rig.SentAt)} ms)");
        Check.True(rig.SentWhileOpen.All(o => o), "every read went out with the window open");
    }

    private static void TypedListShown()
    {
        var rig = New(minRefreshSeconds: 5);
        rig.Open = true;
        rig.Step(500);
        rig.Reply();
        Check.Eq(rig.SentAt.Count, 1, "first read");
        rig.Step(10_000);

        // The player types /bounty list: the reply is shown (nothing eaten) and still applied.
        rig.T.NoteTypedCommand("/bounty list");
        Check.True(rig.T.Reading, "the typed list is captured (shown)");
        Check.False(rig.T.OnChat(Header), "typed reply: the header stays in chat");
        Check.False(rig.T.OnChat("  Lugian Mines: 1 Ascension Coin every 1 kill - you: 0 of 1 kills."), "typed reply: the lines stay in chat");
        Check.False(rig.T.OnChat(Footer), "typed reply: the footer stays in chat");
        Check.False(rig.T.Reading, "and the footer ends it");
        Check.Eq(rig.T.State.Entries.Count, 1, "the typed reply fills the list");

        // It counts as the latest read: a kill right after waits the full interval.
        rig.T.OnKill();
        rig.Step(4000);
        Check.Eq(rig.SentAt.Count, 1, "no read of ours on top of the player's");
        rig.Step(2000);
        Check.Eq(rig.SentAt.Count, 2, "the kill's read comes MinRefreshSeconds after the typed one");
        Check.True(rig.T.OnChat(Header) && rig.T.OnChat(Line) && rig.T.OnChat(Footer), "our own read is eaten again");

        // "/bounty" (where you stand) output is never eaten.
        rig.T.NoteTypedCommand("/bounty");
        Check.False(rig.T.OnChat("Bounty: 1 Ascension Coin - 0 of 1 kills."), "a /bounty line stays in chat");
        Check.False(rig.T.OnChat("Bounty: there is no bounty here."), "'no bounty here' stays in chat");
    }

    private static void TypedDuringCapture()
    {
        var rig = New(minRefreshSeconds: 5);
        rig.Open = true;
        rig.Step(500);
        Check.Eq(rig.SentAt.Count, 1, "our read went out");
        rig.T.NoteTypedCommand(BountyTracker.ListCommand);   // our own echo first (consumed)
        Check.True(rig.T.OnChat(Header), "our reply's header is eaten");

        // The player types /bounty list while our reply is still coming in.
        rig.Step(250);
        rig.T.NoteTypedCommand("/bounty list");
        Check.False(rig.T.OnChat(Line), "after the player asked, reward lines are shown");
        Check.False(rig.T.OnChat(Footer), "and so is the footer");
        Check.False(rig.T.OnChat(Header), "the player's own reply: header shown");
        Check.False(rig.T.OnChat(Line), "the player's own reply: line shown");
        Check.False(rig.T.OnChat(Footer), "the player's own reply: footer shown");
        Check.False(rig.T.OnChat("Bounty complete! +1 Ascension Coin gained."), "award lines are never eaten");

        // On a server without /bounty, the player's typed /bounty during our read: the refusal shows.
        var rig2 = New();
        rig2.Open = true;
        rig2.Step(500);
        rig2.T.NoteTypedCommand("/bounty");
        Check.False(rig2.T.OnChat("Unknown command: bounty"), "the refusal of the player's own /bounty stays in chat");
    }

    private static void OwnEcho()
    {
        var rig = New(minRefreshSeconds: 5);
        rig.Open = true;
        rig.Step(500);
        Check.Eq(rig.SentAt.Count, 1, "our read went out");
        rig.Step(250);
        rig.T.NoteTypedCommand("/bounty list");   // the engine's dispatcher passing our own line back
        Check.True(rig.T.OnChat(Header), "our echo is recognised: the reply is still eaten");
        Check.True(rig.T.OnChat(Line), "reward lines eaten");
        Check.True(rig.T.OnChat(Footer), "footer eaten");

        // An echo that comes too late is taken for the player's: that read is shown, never eaten wrongly.
        rig.Step(10_000);
        Check.Eq(rig.SentAt.Count, 1, "no new read yet (nothing asked)");
        rig.T.Command("refresh");
        rig.Step(500);
        Check.Eq(rig.SentAt.Count, 2, "refresh reads");
        rig.Step(3000);
        rig.T.NoteTypedCommand("/bounty list");
        Check.False(rig.T.OnChat(Header), "a late echo errs on the shown side");
    }

    private static void Matching()
    {
        Check.True(BountyTracker.IsBountyCommand("/bounty"), "/bounty");
        Check.True(BountyTracker.IsBountyCommand("/bounty list"), "/bounty list");
        Check.True(BountyTracker.IsBountyCommand("@Bounty all"), "@bounty all");
        Check.False(BountyTracker.IsBountyCommand("/bountyhunter"), "a longer command is not /bounty");
        Check.False(BountyTracker.IsBountyCommand("/b hello"), "chat");
        Check.True(BountyTracker.IsListCommand("/bounty list") && BountyTracker.IsListCommand("/BOUNTY ALL"), "list commands");
        Check.False(BountyTracker.IsListCommand("/bounty") || BountyTracker.IsListCommand("/bounty list now"), "not list commands");

        var rig = New();
        rig.Open = true;
        rig.Step(500);
        string longLine = "  Lugian Mines: 1 Ascension Coin every 1 kill - you: 0 of 1 kills." + new string(' ', 600) + "x";
        Check.False(rig.T.OnChat(longLine), "a line over 512 characters is never eaten or parsed");
    }
}

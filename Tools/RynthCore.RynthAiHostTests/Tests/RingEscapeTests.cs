using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Empyrean Handler lethal-ring escape (2026-10-17). Silentkelpie heard the Handler's channel at
// 1.7 m, did the old single back-jump (backwards, after a 150 ms turn settle that hadn't taken),
// was still 1.5 m from it 1.6 s later and died 3.2 s after the line. The escape now faces
// directly away and runs, re-aiming away from the Handler, until outside the ring plus a margin,
// then holds attacks until the ring has gone off (running again if the Handler closes back in).
internal static class RingEscapeTests
{
    private const uint Player = 0x50000E21;
    private const uint Handler = 0x80000E31;
    private const uint Lb = 0xA9B40000;
    private const string Channel = "The handler briefly channels infused Empyrean hatred to release a lethal ring.";
    private const int Emote = 0x0C;

    public static void Register(Runner r)
    {
        r.Add("handler ring: the channel inside the ring runs away (faces away, autorun on), no back-jump", RunsAway);
        r.Add("handler ring: outside the ring + margin the run stops, attacks stay held until the ring has gone off", StopsWhenClear);
        r.Add("handler ring: the Handler closing back in before the ring goes off starts the run again", RunsAgain);
        r.Add("handler ring: outside the ring at the channel nothing happens", OutsideStaysPut);
        r.Add("handler ring: death / macro stop ends the run (autorun off, never carried through the portal)", StopEndsRun);
        r.Add("handler ring: a player quoting the line in chat doesn't trigger it", PlayerQuoteIgnored);
        r.Add("chaser ring: an Empyrean Chaser's lethal ring runs away from the Chaser too", ChaserRunsAway);
        r.Add("chaser ring: 'The chaser ...' flees the Chaser even with a Handler nearer", ChaserNamedWins);
        r.Add("ring: a line naming no caster flees the nearest Handler or Chaser; the line is logged once", NearestCasterWithoutSubject);
    }

    private const uint Chaser = 0x80000E32;

    private static CombatManager Rig(float handlerNorthMeters, out LegacyUiSettings s, float? chaserSouthMeters = null)
    {
        FakeHost.Reset();
        Place(Player, "Silentkelpie", 96, 96, creature: false);
        FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
        Place(Handler, "Empyrean Handler", 96, 96 + handlerNorthMeters);
        var ids = new List<uint> { Handler };
        if (chaserSouthMeters is float cs)
        {
            Place(Chaser, "Empyrean Chaser", 96, 96 - cs);
            ids.Add(Chaser);
        }
        s = new LegacyUiSettings { MonsterRange = 60, EnableCombat = true, IsMacroRunning = true };
        // The escape measures from the player: the fake answers GetPlayerId only when created with it.
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, ids.ToArray());
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
        c.ScanNearbyTargets();
        c.HandleCombatTrigger();
        return c;
    }

    private static void RunsAway()
    {
        var c = Rig(1.7f, out _);
        bool jumped = false;
        c.RequestBackJump = (_, _) => { jumped = true; return true; };
        c.HandleChatForRingEvade(Channel, Emote);

        Check.True(FakeHost.AutoRun, "autorun is on");
        Check.False(jumped, "the old back-jump isn't used when autorun and turning are there");
        // The Handler is due north (0°): away is south (180°).
        Check.True(Math.Abs(FakeHost.HeadingDeg - 180.0) < 1.0, $"faces directly away from the Handler (180°, got {FakeHost.HeadingDeg:0})");
        Check.True(c.RingEvadeActive, "the evade holds combat");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatEvade]") && l.Contains("running away")), "logged as a run");

        Check.True(c.Think(), "Think is held while running");
        Check.Eq(c.LastCombatSkipReason, "handler-ring-run", "skip reason: the run");
        Check.True(FakeHost.AutoRun, "still running inside the ring");
    }

    private static void StopsWhenClear()
    {
        var c = Rig(1.7f, out _);
        c.HandleChatForRingEvade(Channel, Emote);
        MovePlayer(96, 96 - 16);   // 17.7 m from the Handler: past the 12 m ring + 4 m margin
        Check.True(c.Think(), "Think still held right after reaching safety (the ring hasn't gone off)");
        Check.False(FakeHost.AutoRun, "the run stops outside the ring");
        Check.Eq(c.LastCombatSkipReason, "handler-ring-wait", "now waiting for the ring");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Ring run stopped") && l.Contains("outside the ring")), "logged why it stopped");
        Check.True(c.RingEvadeActive, "still holding attacks");
    }

    private static void RunsAgain()
    {
        var c = Rig(1.7f, out _);
        c.HandleChatForRingEvade(Channel, Emote);
        MovePlayer(96, 96 - 16);
        c.Think();
        Check.False(FakeHost.AutoRun, "stopped at a safe distance");
        // The Handler follows back inside the ring before it has gone off.
        FakeHost.Positions[Handler] = (Lb | 0x1C, 96f, 96f - 14f, 0f);
        Check.True(c.Think(), "held");
        Check.True(FakeHost.AutoRun, "running again");
        Check.Eq(c.LastCombatSkipReason, "handler-ring-run", "back to the run");
        Check.True(FakeHost.Logs.Any(l => l.Contains("back inside the ring")), "logged");
    }

    private static void OutsideStaysPut()
    {
        var c = Rig(20f, out _);
        c.HandleChatForRingEvade(Channel, Emote);
        Check.False(FakeHost.AutoRun, "no run at 20 m");
        Check.False(c.RingEvadeActive, "combat isn't held");
        Check.True(FakeHost.Logs.Any(l => l.Contains("staying put")), "logged as outside");
    }

    private static void StopEndsRun()
    {
        var c = Rig(1.7f, out _);
        c.HandleChatForRingEvade(Channel, Emote);
        Check.True(FakeHost.AutoRun, "running");
        c.StopRingEscape("died");
        Check.False(FakeHost.AutoRun, "autorun off");
        Check.False(c.RingEvadeActive, "evade cleared");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Ring escape stopped: died")), "logged");
        c.StopRingEscape("again");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("Ring escape stopped")), 1, "a second stop is a no-op");
    }

    private static void PlayerQuoteIgnored()
    {
        var c = Rig(1.7f, out _);
        c.HandleChatForRingEvade("Bob says, \"The handler briefly channels infused Empyrean hatred to release a lethal ring.\"", 0x02);
        Check.False(FakeHost.AutoRun, "a player's quote doesn't start the run");
        Check.False(c.RingEvadeActive, "combat isn't held");
    }

    private const string ChaserChannel = "The chaser briefly channels infused Empyrean hatred to release a lethal ring.";

    private static void ChaserRunsAway()
    {
        // Handler far away (40 m north), a Chaser 2 m south: the Chaser's ring is the danger.
        var c = Rig(40f, out _, chaserSouthMeters: 2f);
        c.HandleChatForRingEvade(ChaserChannel, Emote);
        Check.True(FakeHost.AutoRun, "running from the Chaser");
        // The Chaser is due south (180°): away is north (0°).
        Check.True(Math.Abs(((FakeHost.HeadingDeg % 360) + 360) % 360) < 1.0 || Math.Abs(FakeHost.HeadingDeg - 360) < 1.0,
            $"faces away from the Chaser (0°, got {FakeHost.HeadingDeg:0})");
        Check.True(FakeHost.Logs.Any(l => l.Contains("running away from 'Empyrean Chaser'")), "logged against the Chaser");
    }

    private static void ChaserNamedWins()
    {
        // Handler 1.5 m north, Chaser 3 m south; the emote names the Chaser.
        var c = Rig(1.5f, out _, chaserSouthMeters: 3f);
        c.HandleChatForRingEvade(ChaserChannel, Emote);
        Check.True(FakeHost.Logs.Any(l => l.Contains("running away from 'Empyrean Chaser'")), "the named caster is the one fled");
        Check.True(Math.Abs(FakeHost.HeadingDeg) < 1.0 || Math.Abs(FakeHost.HeadingDeg - 360) < 1.0, $"away from the Chaser (0°, got {FakeHost.HeadingDeg:0})");
    }

    private static void NearestCasterWithoutSubject()
    {
        // A line without "The handler/chaser" (a client that strips it): the nearest caster, the Chaser at 2 m.
        var c = Rig(9f, out _, chaserSouthMeters: 2f);
        c.HandleChatForRingEvade("briefly channels infused Empyrean hatred to release a lethal ring.", Emote);
        Check.True(FakeHost.Logs.Any(l => l.Contains("running away from 'Empyrean Chaser'")), "the nearest caster (the Chaser)");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("lethal-ring line heard")), 1, "the line that triggered is logged");
        c.StopRingEscape("test");
        c.HandleChatForRingEvade("briefly channels infused Empyrean hatred to release a lethal ring.", Emote);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("lethal-ring line heard")), 1, "logged once per session");
    }

    private static void MovePlayer(float x, float y)
    {
        FakeHost.Positions[Player] = (Lb | 0x1C, x, y, 0f);
        FakeHost.PlayerPose = (Lb | 0x1C, x, y, 0f);
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }
}

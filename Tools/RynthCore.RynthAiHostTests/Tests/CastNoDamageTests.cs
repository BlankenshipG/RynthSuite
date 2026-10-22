using System;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Casts that land nothing (player log, 2026-10-11): two Rampagers side by side, every Nether Arc
// VII a "predicted kill" on cast #1 (the learned casts-to-kill was 1), so the bot swapped to the
// other one and back for 30 minutes and the no-damage judge, which every swap resets, never ran.
// A monster swapped off as a predicted kill and then fought again is alive: no more predicted
// swaps off it. And each combat cast logs what the server said in the next few seconds.
internal static class CastNoDamageTests
{
    private const uint Player = 0x50000D01;
    private const uint Lb = 0x5B480000;
    private const uint RampagerWcid = 16916;
    private const uint Wand = 0x8008AB76;
    private const int NetherArc7 = 5367;

    public static void Register(Runner r)
    {
        r.Add("no-damage casts: a monster that survives a predicted kill is not predicted again", SurvivorNotPredictedAgain);
        r.Add("no-damage casts: other monsters still get predicted kills; the memory lapses after 10 min", OthersStillPredicted);
        r.Add("no-damage casts: the chat after a combat cast is logged, swings at us left out, capped, 4 s", CastChatTrace);
        r.Add("no-damage casts (flight 63260): 3 magic casts landing nothing -> one short real sidestep, no cast meanwhile; a hit resets; 30 s apart", ResyncSidestep);
        r.Add("no-damage casts (R-20261021 c788): a corpse selected mid-fight stops the target's health; its chat hits still count, it is not blacklisted", SelectionElsewhereChatHitsCount);
    }

    // ── A corpse selected during the fight (report R-20261021-191720-c788, Conquest) ─────────────
    // "RynthAi interprets a corpse selection during combat as a target for attacks, and stops." The
    // fight target never changed: the server sends health only for the client's selection, so once
    // the player selected a corpse (or themselves) no health came for the monster, and a war spell's
    // hit was seen only in chat, which the no-damage judge didn't count. Three casts that all hit
    // were "no damage": the live monster was blacklisted, dropped, and combat stopped (the log:
    // "3 casts with no damage - blacklisting" at a Curator taking 7,000 a cast).
    private const uint Curator = 0x800F26CD, Marionette = 0x80168E96, Corpse = 0x802B29BD;
    private const uint CuratorWcid = 40001, MarionetteWcid = 40002;
    private const string CuratorHit = "You singe Curator of Torment for 7190 points with Incantation of Flame Bolt.";

    private static CombatManager CorpseRig(LegacyUiSettings s)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;   // installs GetCurrentCombatMode
        FakeHost.CombatModeValue = CombatMode.Magic;
        FakeHost.Names[Player] = "Me";
        FakeHost.Positions[Player] = (Lb | 0x0141, 96f, 96f, 0f);
        FakeHost.PlayerPose = (Lb | 0x0141, 96f, 96f, 0f);
        void Mob(uint id, string name, uint wcid, float north)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x0141, 96f, 96f + north, 0f);
            FakeHost.ItemTypes[id] = 0x10;
            FakeHost.Wcids[id] = wcid;
        }
        Mob(Curator, "Curator of Torment", CuratorWcid, 1.9f);
        Mob(Marionette, "Tormented Marionette", MarionetteWcid, 3f);
        FakeHost.Names[Corpse] = "Corpse of Qosmos, Guardian of Torment";
        FakeHost.Positions[Corpse] = (Lb | 0x0141, 97f, 96f, 0f);
        FakeHost.ItemTypes[Corpse] = 0x200;            // a container
        FakeHost.Bitfields[Corpse] = 0x00002000;       // the corpse flag
        FakeHost.NotAttackable.Add(Corpse);

        s.MonsterRange = 20;
        s.EnableRaycasting = false;    // no raycast warm-up (it suspends the judge)
        s.BlacklistAttempts = 3;
        s.BlacklistCastSettleMs = 0;   // each cast is judged at the next one
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { Curator, Marionette, Corpse });
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, new MonsterDamageStore());
        c.EngageForTest(unchecked((int)Curator));
        FakeHost.SelectedItem = Corpse;   // the player clicks the corpse: no health for the Curator from here on
        return c;
    }

    /// <summary>Five casts at the Curator, each followed by <paramref name="chat"/> (null: nothing), then one more cast
    /// so the fifth is judged too.</summary>
    private static void CastFive(CombatManager c, string? chat)
    {
        for (int i = 0; i < 5; i++)
        {
            c.RecordOffensiveCast(unchecked((int)Curator));   // judges the cast before it, opens this one's window
            if (chat != null) c.HandleChatForDamage(chat, 7);
        }
        c.RecordOffensiveCast(unchecked((int)Curator));
    }

    private static void SelectionElsewhereChatHitsCount()
    {
        var c = CorpseRig(new LegacyUiSettings());
        Check.Eq(FakeHost.SelectedItem, Corpse, "the rig: a corpse is selected");
        CastFive(c, CuratorHit);
        Check.Eq(unchecked((uint)c.activeTargetId), Curator, "five hits seen in chat: the Curator is still the fight target");
        Check.False(FakeHost.Chat.Any(l => l.Contains("with no damage", StringComparison.Ordinal)), "no 'casts with no damage' blacklist");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[Blacklist] 0x800F26CD", StringComparison.Ordinal)), "the Curator was never blacklisted");
        Check.False(FakeHost.Logs.Any(l => l.Contains("one short sidestep", StringComparison.Ordinal)), "no position re-sync sidestep either");
        c.ScanNearbyTargets();
        Check.True(c.ScannedTargets.Any(t => t.Id == unchecked((int)Curator)), "and it stays in the scan");
        Check.False(c.ScannedTargets.Any(t => t.Id == unchecked((int)Corpse)), "the selected corpse is never a candidate");
        // A critical hit has its prefix; it counts the same.
        c.RecordOffensiveCast(unchecked((int)Curator));
        c.HandleChatForDamage("Critical hit! You singe Curator of Torment for 11259 points with Incantation of Flame Bolt.", 7);
        c.RecordOffensiveCast(unchecked((int)Curator));
        Check.Eq(unchecked((uint)c.activeTargetId), Curator, "a critical hit line counts too");

        // The judge still works: casts that land nothing, or hit only another monster, blacklist the target.
        c = CorpseRig(new LegacyUiSettings());
        CastFive(c, null);
        Check.Eq(c.activeTargetId, 0, "no hit line at all: the Curator is blacklisted and dropped, as before");
        Check.True(FakeHost.Chat.Any(l => l.Contains("3 casts with no damage", StringComparison.Ordinal)), "and chat says so");

        c = CorpseRig(new LegacyUiSettings());
        CastFive(c, "You singe Tormented Marionette for 4215 points with Incantation of Flame Bolt.");
        Check.Eq(c.activeTargetId, 0, "hits only on another monster are not the Curator's");

        c = CorpseRig(new LegacyUiSettings());
        CastFive(c, "You singe Curator of Torment for 0 points with Incantation of Flame Bolt.");
        Check.Eq(c.activeTargetId, 0, "a 0-point hit is no damage");
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 30 };
        public readonly MonsterDamageStore Store = new();
        public readonly CombatManager C;
        public readonly uint A = 0x80165DA5, B = 0x80165DA6, D = 0x80165DA7, E = 0x801753EC;
        public DateTime Now = new(2026, 10, 11, 10, 55, 34);

        public Rig()
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[Player] = "Me";
            FakeHost.Positions[Player] = (Lb | 0x018D, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x018D, 96f, 96f, 0f);
            uint[] mobs = { A, B, D, E };
            for (int i = 0; i < mobs.Length; i++)
            {
                FakeHost.Names[mobs[i]] = "Rampager";
                FakeHost.Positions[mobs[i]] = (Lb | 0x018D, 96f + i, 98.7f, 0f);
                FakeHost.ItemTypes[mobs[i]] = 0x10;
                FakeHost.Wcids[mobs[i]] = RampagerWcid;
            }
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, mobs, null);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.TargetClock = () => Now;
            // What the player's store had learned: one Nether Arc VII kills a Rampager.
            for (int i = 0; i < 3; i++)
                Store.RecordKill(Wand, RampagerWcid, "Rampager", "Nether", 7, 1, 435);
            Scan(mobs);
        }

        public void Scan(params uint[] ids) => C.SetScannedTargetsForTests(ids.Select(id => new CombatManager.ScannedTarget
        {
            Id = unchecked((int)id), Distance = 2.7, Angle = 0, Bearing = double.NaN, Name = "Rampager",
        }));

        public void Engage(uint id) => C.EngageForTest(unchecked((int)id));
        public bool Cast() => C.CastAndPredictForTest(Wand, "Nether", 7);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        public int LogCount(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));
    }

    private static void ResyncSidestep()
    {
        var rig = new Rig();
        FakeHost.SetMotionCalls = true;
        var host = FakeHost.Create();
        var c = new CombatManager(host, rig.S, FakeHost.MakeCache(host, Player, new[] { rig.A, rig.B }, null));
        c.SetPlayerId(Player);
        c.TargetClock = () => rig.Now;
        MoveAudit.Reset();

        c.MagicMissForTest(unchecked((int)rig.A));
        c.MagicMissForTest(unchecked((int)rig.B));   // a swap to the other monster doesn't reset the count
        Check.False(c.ResyncActive, "two misses: nothing yet");
        c.ReportDamageOnTarget(unchecked((int)rig.A));
        c.MagicMissForTest(unchecked((int)rig.A));
        c.MagicMissForTest(unchecked((int)rig.A));
        Check.False(c.ResyncActive, "a hit reset the count");
        c.MagicMissForTest(unchecked((int)rig.A));
        Check.True(c.ResyncActive, "three in a row: a re-sync is wanted");
        Check.Eq(rig.LogCount("one short sidestep to re-sync"), 1, "said once");

        Check.True(c.TickResyncStep(), "first tick: the step starts, no cast");
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (0x6500000Fu, true) }), "sidestep right key down");
        rig.Advance(0.2);
        Check.True(c.TickResyncStep(), "still stepping at 200 ms");
        rig.Advance(0.1);
        Check.True(c.TickResyncStep(), "300 ms: key up, settling");
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (0x6500000Fu, true), (0x6500000Fu, false) }), "key up");
        rig.Advance(0.4);
        Check.False(c.TickResyncStep(), "settled: casting may go on");
        Check.Eq(c.ResyncSteps, 1, "one step");

        for (int i = 0; i < 3; i++) c.MagicMissForTest(unchecked((int)rig.A));
        Check.False(c.ResyncActive, "within 30 s: no second step");
        rig.Advance(30);
        for (int i = 0; i < 3; i++) c.MagicMissForTest(unchecked((int)rig.A));
        Check.True(c.ResyncActive, "30 s later: another one");
        c.TickResyncStep();
        Check.True(FakeHost.MotionCalls[2] == (0x65000010u, true), "the other side this time");
    }

    private static void SurvivorNotPredictedAgain()
    {
        var r = new Rig();

        // The ping-pong as it ran: A predicted dead and swapped off, then B.
        r.Engage(r.A);
        Check.True(r.Cast(), "cast #1 at A: predicted kill (learned casts-to-kill 1)");
        r.C.PredictedSwapForTest();
        Check.Eq(r.LogCount("Predicted kill shot 0x80165DA5"), 1, "the swap off A is logged");
        r.Advance(6.3);
        r.Engage(r.B);
        Check.True(r.Cast(), "cast #1 at B: predicted kill");
        r.C.PredictedSwapForTest();

        // A is fought again 13 s later: it didn't die.
        r.Advance(6.3);
        r.Engage(r.A);
        Check.False(r.Cast(), "A survived its predicted kill: no predicted swap off it this time");
        Check.Eq(r.LogCount("Predicted kill of 0x80165DA5 'Rampager' didn't happen"), 1, "said once, naming A");
        Check.True(r.LogCount("No more predicted swaps off it") == 1, "the line says what changes");
        Check.False(r.Cast(), "cast #2 at A: still no predicted swap");
        Check.False(r.Cast(), "cast #3 at A: still none");
        Check.Eq(r.LogCount("didn't happen"), 1, "the line is not repeated per cast");

        // B the same when it comes round.
        r.Engage(r.B);
        Check.False(r.Cast(), "B survived too: no predicted swap off it");
        Check.Eq(r.LogCount("Predicted kill of 0x80165DA6 'Rampager' didn't happen"), 1, "said once for B");
    }

    private static void OthersStillPredicted()
    {
        var r = new Rig();
        r.Engage(r.A);
        r.Cast();
        r.C.PredictedSwapForTest();
        r.Advance(6);
        r.Engage(r.A);
        Check.False(r.Cast(), "A survived: not predicted");

        // D was never swapped off: its cast #1 is a predicted kill as before (E is there to swap to).
        r.Engage(r.D);
        Check.True(r.Cast(), "a monster that never survived a prediction still gets one");
        Check.Eq(r.LogCount("Predicted kill of 0x80165DA7"), 0, "nothing said about D");

        // Ten minutes on, A is forgotten (a respawn can reuse the id).
        r.Advance(11 * 60);
        r.Engage(r.A);
        Check.True(r.Cast(), "after 10 min A gets predicted kills again");
    }

    private static void CastChatTrace()
    {
        var r = new Rig();
        r.C.StartCastChatTrace(NetherArc7, unchecked((int)r.A));
        r.Advance(0.25);
        r.C.TraceCastChat("You say, \"Zhavik Malar\"", 17);
        r.C.TraceCastChat("You evaded Rampager!", 21);
        r.C.TraceCastChat("Rampager grazes your lower leg for 9 points of bludgeoning damage!", 21);
        r.Advance(1.0);
        r.C.TraceCastChat("Rampager resists your spell", 7);
        Check.Eq(r.LogCount("[CombatChat]"), 2, "the incantation and the resist are logged, the swing and the evade are not");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatChat] +250 ms after", StringComparison.Ordinal)
                                         && l.Contains("at 0x80165DA5: type=17", StringComparison.Ordinal)),
                   "the line has the time since the send, the target and the chat type");
        Check.True(FakeHost.Logs.Any(l => l.Contains("+1250 ms", StringComparison.Ordinal) && l.Contains("'Rampager resists your spell'", StringComparison.Ordinal)),
                   "the server's answer is quoted");

        for (int i = 0; i < 20; i++) r.C.TraceCastChat($"line {i}", 0);
        Check.Eq(r.LogCount("[CombatChat]"), 8, "at most 8 lines per cast");

        // A new cast starts a new trace; nothing is logged once its 4 s are up.
        r.C.StartCastChatTrace(NetherArc7, unchecked((int)r.B));
        r.Advance(4.1);
        r.C.TraceCastChat("Your spell fizzled.", 7);
        Check.Eq(r.LogCount("[CombatChat]"), 8, "a line 4.1 s after the send is not logged");
        r.C.TraceCastChat("You cast Corruption VII on Rampager", 7);
        Check.Eq(r.LogCount("[CombatChat]"), 8, "nor any later one until the next cast");
    }
}

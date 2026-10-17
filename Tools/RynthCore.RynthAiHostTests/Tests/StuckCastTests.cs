using System;
using System.Linq;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// In a client log, two casts were held open by ACE's fast-tick turn-to-cast wait (40.8 s and 37 s);
// every cast after them came back UseDone 0x1D and the vitals path re-sent a heal every 2.5 s. A
// client mode change ended the second. ActionPacer now holds every start after a 0x1D until the
// server answers, turns to face a held cast's target, breaks a stuck cast with a jump (then Peace
// mode), waits until landed, and holds starts until a movement change has settled.
internal static class StuckCastTests
{
    private const uint Player = 0x50000F01;
    private const uint Monster = 0x80116781;
    private const int DrainHealthOtherI = 1237;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x6347028Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("stuck cast: after a 'too busy' UseDone nothing starts until the server answers; no timer re-send while our cast is open", NoResendAfterTooBusy);
        r.Add("stuck cast: with no cast of ours open, one probe after 1.5 s, then doubling; a UseDone(0) clears", ProbeWhenNothingOpen);
        r.Add("stuck cast: a held cast is turned to its target at cycle + 1 s, broken by a jump TurnGraceMs after the turn; a normal 4 s cast trips neither", TurnThenJump);
        r.Add("stuck cast: already facing the target (no turn), the jump is at cycle + 2 s", JumpWithoutTurn);
        r.Add("stuck cast: no starts in the air; after landing, no UseDone in 3 s -> Peace mode, 3 s more -> going on", AfterTheJump);
        r.Add("stuck cast: a 'too busy' answer while our cast is open past its cycle + 1 s (and 3 s) breaks it at once; before, it does not", RefusalWhileOpen);
        r.Add("stuck cast: StuckCastMs 0 turns nothing and breaks nothing; no TapJump -> Peace mode", BreakerOffAndNoJump);
        r.Add("move settle: no start in the tick of a movement change; 150 ms and a clear motion queue (or 600 ms) let it go", MoveSettle);
        r.Add("stuck cast: the HP failsafe's forced break jumps once and holds starts until landed", ForcedBreak);
        r.Add("stuck cast: a script's cast is not jumped before ScriptCastMinBreakMs (6 s): answered at 5.5 s, no jump; RynthAi's own is jumped at 5 s", ScriptCastGetsNudgeWindow);
    }

    private static long _t;

    private static RynthCoreHost Setup(int mode = ActionPacer.MagicMode, bool tapJump = true)
    {
        FakeHost.Reset();
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.WeaponCalls = true;      // GetCurrentCombatMode
        FakeHost.CombatModeCalls = true;  // ChangeCombatMode
        FakeHost.TapJumpCalls = tapJump;
        FakeHost.CombatModeValue = mode;
        FakeHost.Names[Player] = "Mira";
        FakeHost.PlayerPose = Me0;
        FakeHost.Positions[Player] = Me0;
        FakeHost.HeadingDeg = 90;                                      // facing east: no held-cast turn (TurnThenJump faces north)
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + 10f, Me0.Y, Me0.Z);   // due east
        var host = FakeHost.Create(Player);
        _t = 90_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        ActionPacer.StuckCastMs = 8000;
        FakeHost.UseDone(0);               // an earlier action's answer: the observer's baseline
        return host;
    }

    private static void Done()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        ActionPacer.StuckCastMs = 8000;
    }

    private static bool May(RynthCoreHost host, HealNeed heal = HealNeed.Heal) =>
        ActionPacer.MayStart(host, "Buff", heal, out _);

    private static void Cast(RynthCoreHost host, string label = "Drain Health Other I") =>
        Check.True(host.CastSpellFor(Monster, DrainHealthOtherI, "Buff", label), "cast sent");

    private static int JumpLines() => FakeHost.Logs.Count(l => l.Contains("stuck cast broken by jump after"));

    private static void Run(RynthCoreHost host, long ms) { for (long s = 0; s < ms; s += 100) { _t += 100; May(host); } }

    private static void ScriptCastGetsNudgeWindow()
    {
        // Control: RynthAi's own cast of a spell with no learned cycle (3 s) is jumped at cycle + 2 s.
        var own = Setup();
        try
        {
            Cast(own);
            Run(own, 5500);
            Check.Eq(JumpLines(), 1, "RynthAi's own cast: jumped by 5.5 s (at 5 s)");
        }
        finally { Done(); }

        // The same cast chosen by a script, answered at 5.5 s: no jump.
        var host = Setup();
        try
        {
            Cast(host);
            ActionPacer.MarkScriptCast(DrainHealthOtherI);
            Run(host, 5500);
            Check.Eq(JumpLines(), 0, "a script's cast: no jump in the first 5.5 s");
            FakeHost.UseDone(0);   // its answer
            Run(host, 3000);
            Check.Eq(JumpLines(), 0, "answered: never jumped");
        }
        finally { Done(); }

        // A script's cast with no answer is still broken, once ScriptCastMinBreakMs has passed.
        var held = Setup();
        try
        {
            Cast(held);
            ActionPacer.MarkScriptCast(DrainHealthOtherI);
            Run(held, ActionPacer.ScriptCastMinBreakMs + 200);
            Check.Eq(JumpLines(), 1, "unanswered: jumped after ScriptCastMinBreakMs");
        }
        finally { Done(); }
    }

    private static void NoResendAfterTooBusy()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;           // the hold alone (the breaker has its own tests)
        try
        {
            Check.True(May(host), "free at first");
            Cast(host);                        // the cast the server will hold open: no UseDone
            _t += 500;
            Check.False(May(host), "our cast is unanswered");

            // Past CastMaxMs (7 s) the pacer used to "go on" and the heal went out into a busy server.
            _t += ActionPacer.CastMaxMs - 500;
            Check.False(May(host), "unanswered past CastMaxMs: still held (the cast is hung, the server is busy with it)");
            Check.True(ActionPacer.ServerBusy, "the server is taken as busy");

            // A cast the player sent by hand is refused: 0x1D. Still nothing goes for 13 s (no timer re-send).
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            bool any = false;
            for (int i = 0; i < 130; i++) { _t += 100; any |= May(host); }
            Check.False(any, "nothing goes for 13 s after the 0x1D while our cast is hung");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Buff waits:") && l.Contains("has had no answer")), "the hold is logged");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("Buff waits:") && l.Contains("has had no answer")), 1, "once");

            // The server finally answers (the cast's own UseDone): everything goes again.
            FakeHost.UseDone(0x402);
            Check.True(May(host), "a UseDone with another error clears the hold");
            Check.False(ActionPacer.ServerBusy, "not busy any more");
            Check.True(FakeHost.Logs.Any(l => l.Contains("the server answered again (UseDone 0x402)")), "logged");
        }
        finally { Done(); }
    }

    private static void ProbeWhenNothingOpen()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // refused: the server is busy with something of no UseDone (a pickup)
            Check.False(May(host), "held right after the 0x1D");
            _t += ActionPacer.BusyProbeMs - 1;
            Check.False(May(host), "held until BusyProbeMs");
            _t += 1;
            Check.True(May(host), "one probe at BusyProbeMs");
            Check.False(May(host), "only one");
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // refused again
            _t += ActionPacer.BusyProbeMs * 2 - 101;
            Check.False(May(host), "the next probe waits twice as long");
            _t += 1;
            Check.True(May(host), "second probe at 2 x BusyProbeMs");
            Check.Eq(FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "no breaker: no cast of ours was left open");
            Cast(host, "Heart Rend");
            _t += 900;
            FakeHost.UseDone(0);
            Check.True(May(host), "a UseDone(0) clears it");
        }
        finally { Done(); }
    }

    private static void TurnThenJump()
    {
        var host = Setup();
        try
        {
            // A normal 4 s cast: answered; it sets the spell's cycle (4 s) and trips nothing.
            Check.True(May(host), "free");
            Cast(host);
            _t += 4000;
            FakeHost.UseDone(0);
            May(host);                         // the answer seen at 4000 ms
            Run(host, 10_000);
            Check.Eq(FakeHost.Turns + FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "a normal 4 s cast: no turn, no jump, no mode change");
            Check.Eq(ActionPacer.NormalCycleMs(DrainHealthOtherI), 4000L, "the cycle learned from it");

            // The next one is held open, facing north (90 deg off): turn at 4 + 1 s, jump TurnGraceMs later.
            FakeHost.HeadingDeg = 0;
            Cast(host);
            long sent = _t;
            while (_t - sent < 4900) { _t += 100; May(host); }
            Check.Eq(FakeHost.Turns, 0, "no turn before cycle + 1 s");
            _t += 100; May(host);
            Check.Eq(FakeHost.Turns, 1, "turned at cycle + 1 s");
            Check.Near(FakeHost.HeadingDeg, 90.0, 0.5, "to face the target (east)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("held cast:") && l.Contains("turning 90 deg to face 0x80116781")), "logged");
            // 2026-10-16: the jump came 1 s after a 91 deg turn, 0.2 s before the cast's answer.
            while (_t - sent < 5000 + ActionPacer.TurnGraceMs - 100) { _t += 100; Check.False(May(host), "held"); }
            Check.Eq(FakeHost.TapJumps, 0, "no jump while the turn has its time (cycle + 2 s is past)");
            _t += 100; May(host);
            Check.Eq(FakeHost.TapJumps, 1, "the breaker jump TurnGraceMs after the turn");
            Check.True(FakeHost.Logs.Any(l => l.Contains($"[Pace] stuck cast broken by jump after {5000 + ActionPacer.TurnGraceMs} ms")), "logged with the time");
            Run(host, 2000);
            Check.Eq(FakeHost.TapJumps, 1, "one jump");
            Check.Eq(FakeHost.Turns, 1, "one turn");

            // The fizzle (0x402): free again once landed.
            FakeHost.UseDone(0x402);
            Check.True(May(host), "free after the cast's UseDone");
        }
        finally { Done(); }
    }

    private static void JumpWithoutTurn()
    {
        var host = Setup();
        try
        {
            // Facing the monster (Setup): no turn goes out.
            Check.True(May(host), "free");
            Cast(host);
            long sent = _t;
            while (FakeHost.TapJumps == 0 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(FakeHost.Turns, 0, "no turn");
            Check.Eq(_t - sent, ActionPacer.DefaultCycleMs + ActionPacer.JumpAfterMs, "the jump at cycle + 2 s");
        }
        finally { Done(); }
    }

    private static void AfterTheJump()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            long sent = _t;
            while (FakeHost.TapJumps == 0 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(_t - sent, ActionPacer.DefaultCycleMs + ActionPacer.JumpAfterMs, "unknown spell cycle: the jump at 3 + 2 s");
            long jumped = _t;
            // In the air: even with the cast's UseDone in, nothing starts.
            FakeHost.PlayerPose = (Me0.Cell, Me0.X, Me0.Y, Me0.Z + 0.6f);
            FakeHost.UseDone(0x402);
            _t += 200;
            Check.False(May(host), "in the air: held even with the UseDone in");
            Check.True(ActionPacer.BreakerAirborne, "airborne");
            _t += 300;
            Check.False(May(host), "still above the take-off height at 500 ms");
            FakeHost.PlayerPose = Me0;
            Check.True(May(host), "landed: free (the cast was answered)");

            // A second hung cast: jump, land, no answer: Peace mode 3 s after landing, then going on 3 s later.
            Cast(host);
            sent = _t;
            while (FakeHost.TapJumps < 2 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(FakeHost.TapJumps, 2, "jumped");
            _t += ActionPacer.JumpLandMinMs; May(host);         // landed (height unchanged)
            long landed = _t;
            while (_t - landed < ActionPacer.BreakerWaitMaxMs - 100) { _t += 100; Check.False(May(host), "held after landing, no answer"); }
            Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Peace yet");
            _t += 100; May(host);
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), ActionPacer.PeaceMode.ToString(), "Peace requested once, 3 s after landing");
            Check.True(FakeHost.Logs.Any(l => l.Contains("stuck cast broken by Peace mode after")), "logged");
            FakeHost.CombatModeValue = ActionPacer.PeaceMode;
            Run(host, ActionPacer.BreakerWaitMaxMs);
            Check.True(FakeHost.Logs.Any(l => l.Contains("no UseDone after the stuck-cast break")), "going on, logged");
            _t += ActionPacer.ModeSettleMs;
            Check.True(May(host), "free after it");
            Check.Eq(FakeHost.CombatModeRequests.Count, 1, "one Peace request");
        }
        finally { Done(); }
    }

    private static void RefusalWhileOpen()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            _t += 2000;
            Cast(host, "Rushed Recovery");          // a heal let ahead of it: refused, 2 s in (a recoil refusal is normal)
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            _t += 100; May(host);
            Check.Eq(FakeHost.TapJumps, 0, "a 0x1D at 2 s: no jump");
            _t += 900; May(host);
            Check.Eq(FakeHost.TapJumps, 0, "3 s with a 0x1D in, not yet RefusalPastCycleMs past its cycle: no jump (it may have landed, its UseDone due)");
            _t += ActionPacer.RefusalPastCycleMs; May(host);
            Check.Eq(FakeHost.TapJumps, 1, "past cycle + 1 s with a 0x1D in: jump at once (before cycle + 2 s)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("was refused 'too busy' while it was open")), "the reason is logged");
        }
        finally { Done(); }
    }

    private static void BreakerOffAndNoJump()
    {
        var host = Setup();
        try
        {
            ActionPacer.StuckCastMs = 0;
            Check.True(May(host), "free");
            Cast(host);
            Run(host, 30_000);
            Check.Eq(FakeHost.Turns + FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "StuckCastMs 0: nothing");
        }
        finally { Done(); }

        host = Setup(tapJump: false);
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            Run(host, 6000);
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), ActionPacer.PeaceMode.ToString(), "no TapJump: Peace mode at the jump time");
            Check.True(FakeHost.Logs.Any(l => l.Contains("no TapJump on this engine")), "logged");
        }
        finally { Done(); }
    }

    private static void MoveSettle()
    {
        var host = Setup();
        try
        {
            FakeHost.AutoRun = true;
            Check.True(May(host), "free");
            host.SetAutoRunBy("Nav", true);
            _t += 1000;
            host.SetAutoRunBy("Nav", false);          // nav's stop goes out this tick
            Check.False(May(host), "no cast in the tick of the stop");
            Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "an attack repeat is not held");
            _t += MoveAudit.MoveSettleMinMs - 1;
            Check.False(May(host), "held under MoveSettleMinMs");
            _t += 1;
            FakeHost.CastBusy = 1;                     // the client still animates the stop
            Check.False(May(host), "the motion queue still busy: held");
            FakeHost.CastBusy = 0;
            Check.True(May(host), "settled: free");
            Check.True(FakeHost.Logs.Any(l => l.Contains("SetAutoRun(False) sent") && l.Contains("has not settled")), "logged");

            host.TurnToHeadingBy("Nav", 45f);
            FakeHost.CastBusy = 1;
            _t += MoveAudit.MoveSettleMaxMs - 1;
            Check.False(May(host), "busy queue: held under MoveSettleMaxMs");
            _t += 1;
            // The gesture gate itself would still hold a busy queue; the settle gate lets go at its max.
            Check.True(!MoveAudit.SettleHolds(host, out _), "the settle gate lets go at MoveSettleMaxMs");
            FakeHost.CastBusy = 0;
        }
        finally { Done(); }
    }

    private static void ForcedBreak()
    {
        var host = Setup();
        try
        {
            Check.True(ActionPacer.ForceBreak(host, "HP failsafe"), "the forced break goes");
            Check.Eq(FakeHost.TapJumps, 1, "one jump");
            Check.False(ActionPacer.ForceBreak(host, "HP failsafe"), "not twice while in the air");
            FakeHost.PlayerPose = (Me0.Cell, Me0.X, Me0.Y, Me0.Z + 0.5f);
            _t += 300;
            Check.False(May(host), "in the air: held");
            FakeHost.PlayerPose = Me0;
            _t += 200;
            Check.True(May(host), "landed: free (nothing of ours was open)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("stuck cast broken by jump after 0 ms") && l.Contains("HP failsafe")), "logged");
        }
        finally { Done(); }
    }
}

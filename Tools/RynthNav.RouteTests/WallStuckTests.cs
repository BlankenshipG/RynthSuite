using System;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.RouteTests;

// Stuck at a wall (2026-11-07, Silentkelpie: "routes are still getting stuck at walls that could be
// jumped"). The jump was a tap (the smallest hop) and moved 0.0 u every time; a walk swinging between
// headings against a wall never counted as stuck, and then it stood 25 s until the watchdog.
internal static partial class Program
{
    private static void RunWallStuck()
    {
        Run(nameof(StuckWhileRunning), StuckWhileRunning);
        Run(nameof(StuckWhileSwingingAgainstAWall), StuckWhileSwingingAgainstAWall);
        Run(nameof(TurningOnTheSpotIsNotStuckAtOnce), TurningOnTheSpotIsNotStuckAtOnce);
        Run(nameof(NotTryingToMoveStartsTheWatchAgain), NotTryingToMoveStartsTheWatchAgain);
        Run(nameof(ChargedJumpThenFullThenStepRound), ChargedJumpThenFullThenStepRound);
        Run(nameof(TapJumpWithoutChargedJump), TapJumpWithoutChargedJump);
        Run(nameof(JumpingHardIsFullPowerThreeTimes), JumpingHardIsFullPowerThreeTimes);
        Run(nameof(JumpFromSkillAndBurden), JumpFromSkillAndBurden);
    }

    // Runs ticks of 33 ms from t, the character at (x, y), driving each tick (running or turning).
    private static bool Ticks(StuckWatch w, ref long t, long ms, double x, double y, Func<long, bool> running, out double moved, out long win)
    {
        moved = 0; win = 0;
        for (long end = t + ms; t < end; t += 33)
        {
            if (w.Check(t, x, y, out moved, out win)) return true;
            w.Wanted(t, running(t));
        }
        return false;
    }

    private static void StuckWhileRunning()
    {
        var w = new StuckWatch();
        long t = 1000;
        bool stuck = Ticks(w, ref t, 1500, 10, 10, _ => true, out double moved, out long win);
        Check(stuck && moved < 0.01 && win >= StuckWatch.RunWindowMs && win < StuckWatch.RunWindowMs + 100, $"running into a wall: stuck after {win} ms");

        // Running on open ground is never stuck.
        w = new StuckWatch(); t = 1000;
        bool any = false;
        for (int i = 0; i < 200; i++, t += 33)
        {
            if (w.Check(t, i * 0.15, 0, out _, out _)) any = true;   // 4.5 u/s
            w.Wanted(t, true);
        }
        Check(!any, "running on open ground: never stuck");
    }

    private static void StuckWhileSwingingAgainstAWall()
    {
        // 11-07 at Yaraq: run a moment, turn a moment, swinging between headings, not moving. The old
        // watch started again on every turning tick and never saw it.
        var w = new StuckWatch();
        long t = 1000;
        bool stuck = Ticks(w, ref t, 3500, 5, 5, tt => (tt / 300) % 2 == 0, out double moved, out long win);
        Check(stuck && win < 2000, $"swinging against a wall (running and turning by turns): stuck after {win} ms, moved {moved:F1}");
    }

    private static void TurningOnTheSpotIsNotStuckAtOnce()
    {
        // A half turn on the spot takes about a second: not stuck. Turning on the spot for 3 s is.
        var w = new StuckWatch();
        long t = 1000;
        bool early = Ticks(w, ref t, 2500, 0, 0, _ => false, out _, out _);
        Check(!early, "turning on the spot for 2.5 s: not stuck yet");
        bool late = Ticks(w, ref t, 1000, 0, 0, _ => false, out _, out long win);
        Check(late && win >= StuckWatch.TurnWindowMs, $"turning on the spot for 3 s: stuck ({win} ms)");
    }

    private static void NotTryingToMoveStartsTheWatchAgain()
    {
        var w = new StuckWatch();
        long t = 1000;
        Ticks(w, ref t, 900, 0, 0, _ => true, out _, out _);
        // Opening a door, waiting for a plan: no drive for half a second.
        for (long end = t + 600; t < end; t += 33) Check(!w.Check(t, 0, 0, out _, out _), "not driving: never stuck");
        bool stuck = Ticks(w, ref t, 900, 0, 0, _ => true, out _, out _);
        Check(!stuck, "running again: a new window (900 ms isn't stuck)");
    }

    private static void ChargedJumpThenFullThenStepRound()
    {
        var j = new WallJump();
        Check(j.CanTry && !j.Busy, "a new spot: a jump to try");
        long t = 0;
        int c1 = j.StartCharged(t);
        Check(c1 == 700 && j.Now == WallJump.Phase.Charging && j.Tries == 1, $"first jump: charged {c1} ms (a strong jump)");
        Check(j.Tick(t + 300) == WallJump.Step.Hold, "charging: the key stays down");
        Check(j.Tick(t + 700) == WallJump.Step.Release && j.Now == WallJump.Phase.Flying, "charged: let go");
        Check(j.Tick(t + 1100) == WallJump.Step.Hold, "in the air");
        Check(j.Tick(t + 700 + WallJump.FlightMs) == WallJump.Step.Landed && !j.Busy, "landed: the walk looks again");
        j.Moved();   // the next window showed progress: it cleared the wall
        Check(j.Tries == 0 && j.CanTry, "it got over: the next spot gets its jumps again");

        t = 5000;
        j.StartCharged(t); j.Tick(t + 700); j.Tick(t + 700 + WallJump.FlightMs);
        int c2 = j.StartCharged(t + 3000);
        Check(c2 == 1000 && j.Tries == 2, $"still stuck at the same spot: a full jump ({c2} ms)");
        j.Tick(t + 4000); j.Tick(t + 4000 + WallJump.FlightMs);
        Check(!j.CanTry, "both jumps tried at this spot: step round it");
        j.Reset();
        Check(j.CanTry && j.Tries == 0, "a new leg: jumps again");
    }

    private static void TapJumpWithoutChargedJump()
    {
        var j = new WallJump();
        j.StartTap(100);
        Check(j.Now == WallJump.Phase.Flying && j.Charge == 0, "no charged jump in the engine: a tap, already in the air");
        Check(j.Tick(100 + WallJump.FlightMs) == WallJump.Step.Landed, "the tap lands");
    }

    // "it should use more power to jump if needed": where the route graph has no walking way on the
    // same land, every jump is a full one, three at each spot.
    private static void JumpingHardIsFullPowerThreeTimes()
    {
        var j = new WallJump { Hard = true };
        long t = 0;
        var charges = new System.Collections.Generic.List<int>();
        while (j.CanTry)
        {
            charges.Add(j.StartCharged(t));
            j.Tick(t + 1000); j.Tick(t + 1000 + WallJump.FlightMs);
            t += 3000;
        }
        Check(string.Join(",", charges) == "1000,1000,1000", "jumping hard: three full jumps at a spot (" + string.Join(",", charges) + ")");
        j.Reset();
        Check(j.Hard && j.CanTry, "a new spot keeps jumping hard; the walk turns it off when a walking way is found");
        j.Hard = false;
        Check(j.StartCharged(0) == 700 && j.Plan.Length == 2, "back to walking: a strong jump, then a full one");
    }

    // "the jump on routes should take your buffed jump into account as well as your burden".
    private static void JumpFromSkillAndBurden()
    {
        var m = new JumpModel(300, 0.5);
        Near(m.FullHeight, 300 / 1600.0 * 22.2 + 0.05, 1e-9, "Jump 300, half burden: AC's full jump height");
        Near(m.Height(0.5), m.FullHeight / 2, 1e-9, "half a bar: half the height");
        Near(new JumpModel(300, 1.5).FullHeight, m.FullHeight * 0.5, 1e-9, "150% burden: half the height");
        Check(new JumpModel(300, 2.1).TooBurdened && new JumpModel(300, 2.1).PlanFor(1.0, false, out _).Length == 0, "200% burden and over: no jump");
        Near(new JumpModel(10, 0.1).Height(0.1), 0.35, 1e-9, "never under 0.35 m");

        int c = m.ChargeFor(1.5);
        Check(c == 450, $"a 1.5 m fence, Jump 300: a 450 ms bar ({c})");
        int[] p = m.PlanFor(1.5, false, out string why);
        Check(p.Length == 2 && p[0] == 450 && p[1] == 1000 && why.Contains("1.5 m high"), $"just enough, then a full one ({string.Join(",", p)}: {why})");
        p = m.PlanFor(5.0, false, out why);
        Check(p.Length == 0 && why.Contains("your jump clears about"), $"a 5 m wall: no jump, step round ({why})");
        Check(new JumpModel(300, 1.6).PlanFor(2.0, false, out _).Length == 0 && m.PlanFor(2.0, false, out _).Length == 2,
            "the same 2 m wall: jumped light, not at 160% burden");
        p = m.PlanFor(double.NaN, false, out _);
        Check(p.Length == 2 && p[0] >= 700 && p[1] == 1000, $"height not known: a strong jump, then a full one ({string.Join(",", p)})");
        Check(string.Join(",", m.PlanFor(double.NaN, true, out _)) == "1000,1000,1000", "not known, jumping hard: three full ones");
        Check(!new JumpModel(0, 0.2).Known, "no skill read: not known (the default jumps)");

        var j = new WallJump();
        j.SetSpot(new[] { 450, 1000 });
        Check(j.StartCharged(0) == 450, "the spot's plan is used");
        j.Tick(450); j.Tick(450 + WallJump.FlightMs);
        j.Moved();
        Check(j.Plan.Length == 2 && j.Plan[0] == 700, "moved on: the next spot plans again");
        j.SetSpot(System.Array.Empty<int>());
        Check(!j.CanTry, "nothing clears it: no jump, step round");
    }
}


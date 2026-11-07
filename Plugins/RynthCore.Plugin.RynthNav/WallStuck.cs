using System;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// Is the walk stuck (2026-11-07)? Until now only a tick that ran counted: a tick turning in place
/// started the watch again. On 11-07 at Yaraq the walk swung between headings 58, 113, 338 and 119,
/// running a moment and turning a moment, for three seconds against a wall, and the watch never saw
/// 1.2 s of running in a row; then it stood for 25 s and the 30 s progress watchdog stopped the goto.
/// Now every tick the walk wants to move counts (running or turning): moved less than
/// <see cref="MoveUnits"/> over <see cref="RunWindowMs"/> with some running in it, or over
/// <see cref="TurnWindowMs"/> of turning only, is stuck. A tick that doesn't want to move (opening a
/// door, waiting for a plan) starts the watch again. Host-free: the route tests drive it.
/// </summary>
internal sealed class StuckWatch
{
    /// <summary>A window with running in it: this long...</summary>
    public const long RunWindowMs = 1200;
    /// <summary>...or this long turning only (a half turn on the spot takes about a second).</summary>
    public const long TurnWindowMs = 3000;
    /// <summary>...and moved less than this = stuck (a run covers 4+ u/s).</summary>
    public const double MoveUnits = 0.6;
    /// <summary>No drive asked for this long: the walk isn't trying to move.</summary>
    public const long WantGapMs = 150;

    private long _winMs;
    private double _x, _y;
    private bool _ranInWindow;
    private long _lastWantMs = long.MinValue / 2;

    /// <summary>The walk drove this tick: running, or turning in place.</summary>
    public void Wanted(long now, bool running)
    {
        _lastWantMs = now;
        if (running) _ranInWindow = true;
    }

    public void Reset()
    {
        _winMs = 0;
        _ranInWindow = false;
    }

    /// <summary>
    /// Once a walking tick, before steering: true when stuck (the window is over and the character moved
    /// less than <see cref="MoveUnits"/> in it). <paramref name="moved"/> is how far it went in the window and
    /// <paramref name="windowMs"/> how long the window was, both 0 while no window has ended this tick.
    /// </summary>
    public bool Check(long now, double x, double y, out double moved, out long windowMs)
    {
        moved = 0;
        windowMs = 0;
        bool wanted = now - _lastWantMs <= WantGapMs;
        if (!wanted || _winMs == 0)
        {
            _winMs = now; _x = x; _y = y; _ranInWindow = false;
            return false;
        }
        long need = _ranInWindow ? RunWindowMs : TurnWindowMs;
        long elapsed = now - _winMs;
        if (elapsed < need) return false;
        windowMs = elapsed;   // only when a window ended
        moved = Math.Sqrt((x - _x) * (x - _x) + (y - _y) * (y - _y));
        _winMs = now; _x = x; _y = y; _ranInWindow = false;
        return moved < MoveUnits;
    }
}

/// <summary>
/// The running jump at a spot the walk is stuck at (2026-11-07). It used to be one tap of the jump
/// key: a tap is the smallest hop AC has (jump height is how long the key is held), and in every
/// log the character had moved 0.0 u 1.2 s after it. Now it charges like a held spacebar
/// (<see cref="ChargeMs"/>: a medium jump, then a full one), released with forward and run held so it
/// carries the run's momentum (the engine's LaunchJumpWithMotion, as RynthAi's /mt jump does; full
/// jumps only, three of them, when jumping <see cref="Hard"/>), then
/// <see cref="FlightMs"/> in the air before the stuck watch starts again. Only after both jumps at
/// one spot does the walk step round it. Host-free: the route tests drive it.
/// </summary>
internal sealed class WallJump
{
    /// <summary>The charge of each try at one spot (ms; 1000 is a full bar): a strong jump, then a full one.</summary>
    public static readonly int[] ChargeMs = { 700, 1000 };
    /// <summary>
    /// Jumping hard (2026-11-07, "it should use more power to jump if needed"): where the route graph has no
    /// walking way to a spot on the same land (a wall or a ledge the navmesh doesn't cross), every jump is a
    /// full one, three at each spot.
    /// </summary>
    public static readonly int[] HardChargeMs = { 1000, 1000, 1000 };

    /// <summary>Jumping hard: <see cref="HardChargeMs"/> instead of <see cref="ChargeMs"/>.</summary>
    public bool Hard { get; set; }
    private int[]? _spot;
    /// <summary>The charges tried at one spot, in order: this spot's own plan (<see cref="SetSpot"/>), else the default.</summary>
    public int[] Plan => _spot ?? (Hard ? HardChargeMs : ChargeMs);

    /// <summary>
    /// This spot's jumps, worked out from the character's jump and what is in the way (JumpModel.PlanFor):
    /// empty when no jump clears it (too high, too burdened), so the walk steps round at once.
    /// </summary>
    public void SetSpot(int[] plan) { _spot = plan; }
    /// <summary>Time in the air before the walk looks again.</summary>
    public const long FlightMs = 900;

    public enum Phase { Idle, Charging, Flying }
    public enum Step { Hold, Release, Landed }

    private long _releaseAt, _landAt;

    public Phase Now { get; private set; } = Phase.Idle;
    /// <summary>Jumps tried at this spot.</summary>
    public int Tries { get; private set; }
    public bool Busy => Now != Phase.Idle;
    public bool CanTry => Tries < Plan.Length;
    /// <summary>The charge of the jump under way (0 for a tap).</summary>
    public int Charge { get; private set; }

    /// <summary>A charged jump: the jump key is down from now. Returns the charge (ms).</summary>
    public int StartCharged(long now)
    {
        Charge = Plan[Math.Min(Tries, Plan.Length - 1)];
        Tries++;
        Now = Phase.Charging;
        _releaseAt = now + Charge;
        return Charge;
    }

    /// <summary>A tap (no charged jump in this engine): already in the air.</summary>
    public void StartTap(long now)
    {
        Charge = 0;
        Tries++;
        Now = Phase.Flying;
        _landAt = now + FlightMs;
    }

    /// <summary>Each tick while <see cref="Busy"/>: Release once (let go of the key), then Landed once.</summary>
    public Step Tick(long now)
    {
        if (Now == Phase.Charging && now >= _releaseAt)
        {
            Now = Phase.Flying;
            _landAt = now + FlightMs;
            return Step.Release;
        }
        if (Now == Phase.Flying && now >= _landAt)
        {
            Now = Phase.Idle;
            return Step.Landed;
        }
        return Step.Hold;
    }

    /// <summary>It moved on: the next stuck spot gets its jumps again.</summary>
    public void Moved() { if (!Busy) { Tries = 0; _spot = null; } }

    public void Reset()
    {
        Now = Phase.Idle;
        Tries = 0;
        Charge = 0;
        _spot = null;
    }
}

/// <summary>
/// How high this character jumps right now (2026-11-07, Silentkelpie: "the jump on routes should take your
/// buffed jump into account as well as your burden"). AC's jump, as the server works it out (ACE's
/// MovementSystem.GetJumpHeight): height = burden factor x (Jump / (Jump + 1300) x 22.2 + 0.05) x power, at
/// least 0.35 m, power being how full the bar is (held 0 to 1 s). The burden factor is 1 up to 100% burden,
/// falls to 0 at 200%, where no jump leaves the ground. Jump is the buffed skill. Burden is what you carry over
/// what your Strength carries (150 per point, 30 more per point for each carrying augmentation).
/// Host-free: the route tests drive it.
/// </summary>
internal sealed class JumpModel
{
    /// <summary>The Jump skill's id.</summary>
    public const uint JumpSkillId = 22;
    /// <summary>Feet over the top of what is jumped (m).</summary>
    public const double Clearance = 0.35;
    /// <summary>The navmesh climbs 1 m (RynthNav.Baker agentMaxClimb): what stops the walk is higher.</summary>
    public const double MeshClimb = 1.0;
    /// <summary>The least charge sent (a shorter press is a tap).</summary>
    public const int MinChargeMs = 200;

    /// <summary>The buffed Jump skill (0: not known).</summary>
    public int Skill { get; }
    /// <summary>Burden over capacity (1.0 = 100%; NaN: not known).</summary>
    public double Burden { get; }

    public JumpModel(int skill, double burden) { Skill = Math.Max(0, skill); Burden = burden; }

    public bool Known => Skill > 0;
    public bool BurdenKnown => !double.IsNaN(Burden);
    /// <summary>Too burdened to jump at all (200% or more).</summary>
    public bool TooBurdened => BurdenKnown && Burden >= 2.0;

    public static double BurdenFactor(double burden) =>
        double.IsNaN(burden) || burden < 1.0 ? 1.0 : burden < 2.0 ? 2.0 - burden : 0.0;

    /// <summary>The jump's height at <paramref name="power"/> (0..1), in metres.</summary>
    public double Height(double power)
    {
        double p = Math.Clamp(power, 0, 1);
        double h = BurdenFactor(Burden) * (Skill / (Skill + 1300.0) * 22.2 + 0.05) * p;
        return Math.Max(h, 0.35);
    }

    /// <summary>A full jump's height.</summary>
    public double FullHeight => Height(1.0);

    /// <summary>The bar to fill (ms) for feet <see cref="Clearance"/> over something <paramref name="top"/> m high; 0 = none does.</summary>
    public int ChargeFor(double top)
    {
        double need = top + Clearance;
        double full = BurdenFactor(Burden) * (Skill / (Skill + 1300.0) * 22.2 + 0.05);
        if (TooBurdened || full <= 0 || need > full) return 0;
        return Math.Clamp((int)Math.Ceiling(need / full * 1000.0 / 50.0) * 50, MinChargeMs, 1000);
    }

    /// <summary>
    /// The jumps for one stuck spot. <paramref name="top"/>: how high the thing in the way is (NaN: not known,
    /// a live object or no geometry). Known and jumpable: just enough, then a full one. Too high or too
    /// burdened: none (step round at once). Not known: from the mesh's climb up, then full ones.
    /// </summary>
    public int[] PlanFor(double top, bool hard, out string why)
    {
        if (TooBurdened) { why = $"too burdened to jump ({Burden * 100:F0}%)"; return Array.Empty<int>(); }
        if (!double.IsNaN(top))
        {
            int c = ChargeFor(top);
            if (c == 0) { why = $"{top:F1} m high; your jump clears about {FullHeight - Clearance:F1} m (Jump {Skill}{BurdenNote})"; return Array.Empty<int>(); }
            why = $"{top:F1} m high; a {c} ms jump clears it (Jump {Skill}{BurdenNote})";
            return c >= 1000 ? new[] { 1000, 1000 } : new[] { c, 1000 };
        }
        int first = Math.Max(ChargeFor(MeshClimb + 0.6), 700);
        if (first == 0 || hard) first = 1000;
        why = $"height not known; from {first} ms (Jump {Skill}{BurdenNote}: a full jump clears about {FullHeight - Clearance:F1} m)";
        return hard ? new[] { 1000, 1000, 1000 } : new[] { first, 1000 };
    }

    private string BurdenNote => BurdenKnown ? $", burden {Burden * 100:F0}%" : "";
}


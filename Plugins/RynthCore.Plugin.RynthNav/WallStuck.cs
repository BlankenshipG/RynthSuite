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
    /// <summary>The charges tried at one spot, in order.</summary>
    public int[] Plan => Hard ? HardChargeMs : ChargeMs;
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
    public void Moved() { if (!Busy) Tries = 0; }

    public void Reset()
    {
        Now = Phase.Idle;
        Tries = 0;
        Charge = 0;
    }
}

using System;
using System.Collections.Generic;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Running into a wall: the walk is running but the character isn't moving. The navmesh is baked
// with a 2 m agent radius (RynthNav.Baker), which seals gaps under ~4 m and leaves some ground in
// little islands (Shoushi's plaza, where the Town Network drops you, 10-08); where the plan runs
// out the walk heads straight for its goal for a moment (BlindPushTicks), and that, or a wall the
// mesh doesn't show, used to mean running into it until the 30 s progress watchdog. Now: blocked
// for StuckWindowMs = (since 2026-10-30, the first block at a spot: a running jump on the same
// heading, over a fence or a step) turn away toward open ground (WalkSteer.UnstickHeading), remember the spot as
// something to keep out of (the next plan bends round it, like round a portal), plan again, and
// after StuckMaxTries on one leg without progress stop and say where.
//
// 11-01, at a bridge the navmesh doesn't show (flat open ground in the tile, nothing in the way):
// it turned 120 degrees off, alternating sides, ran 1.6 s, planned the same path again and ran back
// into the same spot, four times in 20 s, then stopped. The remembered spots now make each new
// plan go round what it ran into.
//
// 11-07 (Silentkelpie: "routes are still getting stuck at walls that could be jumped"): the jump was a
// tap of the key, the smallest hop AC has, and the character had moved 0.0 u after it every time; and a
// walk swinging between headings against a wall never counted as blocked. Now (WallStuck.cs) the stuck
// watch counts turning too, and the jump is charged (0.6 s, then a full 1 s) and let go with run and
// forward held. Both jumps at a spot come before stepping round it.
public sealed partial class RynthNavPlugin
{
    private const long UnstickMs = 1600;           // then turn away from it and run this long (~0.5 s of it turning)
    private const int StuckMaxTries = 4;           // blocked this often on a leg without progress = stop
    private const double BlockedAheadUnits = 3.5;  // the remembered spot: this far ahead of where it stuck...
    private const double BlockedRadiusUnits = 2.5; // ...this big (the character stays outside it)
    private const int BlockedMaxSpots = 12;
    private const double UnstickOpenUnits = 6.0;   // a way out is open ground this far along it

    private double _lastDriveDeg;
    private long _unstickUntilMs;
    private double _unstickDeg, _jumpDeg;
    private int _stuckTries, _stuckSide = 1;
    private readonly StuckWatch _stuck = new();
    private readonly WallJump _jump = new();
    private readonly List<PortalAvoid.Spot> _blockedSpots = new();

    /// <summary>DriveTo for the walk's own steering: remembers which way, and that it wanted to move.</summary>
    private void WalkDrive(double deg)
    {
        DriveTo(deg, _steerOpen);
        _lastDriveDeg = deg;
        _stuck.Wanted(NowMs, running: !_steerOpen.TurnInPlace);
    }

    /// <summary>Lets go of a charged jump: with run and forward held (the run's momentum), else a plain release.</summary>
    private void ReleaseJump()
    {
        if (Host.HasLaunchJumpWithMotion) Host.LaunchJumpWithMotion(shift: true, holdW: true, holdX: false, holdZ: false, holdC: false);
        else if (Host.HasDoJump) Host.DoJump(true);
    }

    /// <summary>A jump that is charging or in the air ends (a new leg, the goto over): let go of the key.</summary>
    private void CancelJump()
    {
        if (_jump.Now == WallJump.Phase.Charging) ReleaseJump();
        _jump.Reset();
    }

    /// <summary>
    /// Nothing between here and (<paramref name="tx"/>, <paramref name="ty"/>) as far as the navmesh
    /// shows: every metre of the line, bar 2 m at each end (we and a portal stand off the eroded
    /// mesh), is within 1.5 m of walkable ground. A wall leaves a gap of twice the agent radius plus
    /// its thickness, over 4 m, so its middle fails. No query (no tiles): true, as before.
    /// </summary>
    private bool StraightOnMesh(double tx, double ty)
    {
        if (_query == null) return true;
        var filter = new DtQueryDefaultFilter();
        double dx = tx - _wx, dy = ty - _wy, len = Math.Sqrt(dx * dx + dy * dy);
        for (double s = 2.0; s < len - 2.0; s += 1.0)
        {
            var pt = new RcVec3f((float)(_wx + dx * s / len), (float)_wz, (float)(_wy + dy * s / len));
            _query.FindNearestPoly(pt, new RcVec3f(1.5f, 4, 1.5f), filter, out long r, out _, out _);
            if (r == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// A straight walk from (ax, ay) to (bx, by) stays on the navmesh (Detour's raycast along the
    /// polygons from the one underfoot: no mesh edge, so at least the 2 m agent radius from every
    /// wall it knows) and out of every circle being kept out of. False without a query or ground underfoot.
    /// </summary>
    private bool WalkableStraight(double ax, double ay, double bx, double by)
    {
        if (_query == null) return false;
        for (int i = 0; i < _avoid.Count; i++)
        {
            PortalAvoid.Spot s = _avoid.Spots[i];
            if (PortalAvoid.SegmentDistance(s.X, s.Y, ax, ay, bx, by) < s.R + PortalAvoid.SteerMargin) return false;
        }
        // The default filter: the circles were checked above (the avoid filter drops every big
        // polygon that merely reaches into one, which would read as a wall far from the line).
        var filter = new DtQueryDefaultFilter();
        var start = new RcVec3f((float)ax, (float)_wz, (float)ay);
        _query.FindNearestPoly(start, new RcVec3f(2.5f, 4, 2.5f), filter, out long sRef, out RcVec3f sPt, out _);
        if (sRef == 0) return false;
        Span<long> path = stackalloc long[64];
        var st = _query.Raycast(sRef, sPt, new RcVec3f((float)bx, sPt.Y, (float)by), filter, out float t, out _, path, out _, 64);
        return st.Succeeded() && t >= 1.0f;
    }

    /// <summary>A new leg (or goto): nothing blocked yet.</summary>
    private void ResetBlocked()
    {
        if (_doorStep != DoorStep.None) ResetDoor();
        _stuck.Reset();
        CancelJump();
        _unstickUntilMs = 0; _stuckTries = 0;
        _blockedSpots.Clear();
    }

    /// <summary>
    /// Once per walking tick, before steering. True when it drove this tick (stepping round
    /// something) or finished the goto; the caller returns.
    /// </summary>
    private bool StepBlocked()
    {
        long now = NowMs;
        // A jump under way: hold the heading, let go of the key when charged, look again once landed.
        if (_jump.Busy)
        {
            DriveTo(_jumpDeg);
            switch (_jump.Tick(now))
            {
                case WallJump.Step.Release:
                    ReleaseJump();
                    break;
                case WallJump.Step.Landed:
                    _stuck.Reset();
                    return false;
            }
            lock (_gate) _status = $"blocked: jumping ({_jump.Tries}/{WallJump.ChargeMs.Length})";
            return true;
        }
        if (_unstickUntilMs > now)
        {
            DriveTo(_unstickDeg, _steerOpen);
            lock (_gate) _status = $"blocked: stepping round it ({_stuckTries}/{StuckMaxTries})";
            return true;
        }
        if (_unstickUntilMs != 0)
        {
            // Stepped aside: plan again from here (and allow one more push where the plan ends).
            _unstickUntilMs = 0;
            lock (_gotoGate) _gotoPath = null;
            _blindTicks = 0; _replanTick = 0;
            _stuck.Reset();
            return false;
        }
        if (!_stuck.Check(now, _wx, _wy, out double moved, out long windowMs))
        {
            if (windowMs > 0) _jump.Moved();   // a window ended with progress: the next spot gets its jumps
            return false;
        }

        string at = NavCoords.Fmt(NavCoords.NsFromWorld(_wy), NavCoords.EwFromWorld(_wx));
        // A closed door in front of us: open it first (GotoDoor works it from the next tick).
        if (OpenDoorInFront())
        {
            AutoRun.Halt();
            lock (_gate) _status = "blocked: opening a door";
            return true;
        }
        // First jump on the same heading, charged (a fence, a step, a low wall the mesh smooths over),
        // twice at one spot (a medium jump, then a full one); only then step round it.
        if (_jump.CanTry && (Host.HasCommenceJump || Host.HasTapJump))
        {
            _jumpDeg = _lastDriveDeg;
            DriveTo(_jumpDeg);
            string how;
            if (Host.HasCommenceJump && Host.CommenceJump())
                how = $"a charged jump ({_jump.StartCharged(now)} ms)";
            else
            {
                Host.TapJump();
                _jump.StartTap(now);
                how = "a tap jump (no charged jump in this engine)";
            }
            Host.Log($"[RynthNav] walk: blocked at {at} heading {_jumpDeg:F0} (moved {moved:F1}u in {windowMs / 1000.0:F1} s): {how}, try {_jump.Tries}/{WallJump.ChargeMs.Length}");
            lock (_gate) _status = $"blocked: jumping ({_jump.Tries}/{WallJump.ChargeMs.Length})";
            return true;
        }
        _jump.Reset();
        if (++_stuckTries > StuckMaxTries)
        {
            string why; lock (_gate) why = _status;
            FinishGoto($"stopped: blocked at {at} ({StuckMaxTries} tries to get round it, jumping first each time; something in the way the map doesn't show) ({why})");
            return true;
        }
        // Remember what it ran into, so the next plans go round it (RefreshAvoid adds these).
        double rad = _lastDriveDeg * Math.PI / 180.0;
        if (_blockedSpots.Count >= BlockedMaxSpots) _blockedSpots.RemoveAt(0);
        _blockedSpots.Add(new PortalAvoid.Spot(_wx + Math.Sin(rad) * BlockedAheadUnits, _wy + Math.Cos(rad) * BlockedAheadUnits,
                                               BlockedRadiusUnits, "something in the way"));
        RefreshAvoid(force: true);

        _stuckSide = -_stuckSide;
        _unstickDeg = WalkSteer.UnstickHeading(_lastDriveDeg, _stuckSide, h =>
        {
            double r = h * Math.PI / 180.0;
            return WalkableStraight(_wx, _wy, _wx + Math.Sin(r) * UnstickOpenUnits, _wy + Math.Cos(r) * UnstickOpenUnits);
        });
        _unstickUntilMs = now + UnstickMs;
        Host.Log($"[RynthNav] walk: blocked at {at} heading {_lastDriveDeg:F0} (moved {moved:F1}u in {windowMs / 1000.0:F1} s, jumped {WallJump.ChargeMs.Length}x); "
               + $"turning to {_unstickDeg:F0}, keeping out of that spot ({_blockedSpots.Count} so far) and planning again ({_stuckTries}/{StuckMaxTries})");
        DriveTo(_unstickDeg, _steerOpen);
        lock (_gate) _status = $"blocked: stepping round it ({_stuckTries}/{StuckMaxTries})";
        return true;
    }
}

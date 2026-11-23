using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// Jumps that know the character and the obstacle (2026-11-07, Silentkelpie: "the jump on routes should take
// your buffed jump into account as well as your burden to plan a route and a jump to get over things to by
// pass doors and other obstacles"). When the walk is stuck, or a door on the way is locked:
//   - how high this character jumps now (JumpModel: the buffed Jump skill, the burden),
//   - how high the thing in front is (RynthAi's ObstacleAhead: rays through the dats' static geometry),
// then the jump that just clears it (and a full one), or none when it can't (step round at once, or
// plan round the locked door). Tick thread.
public sealed partial class RynthNavPlugin
{
    /// <summary>How far ahead the stuck walk looks for what stopped it (world units).</summary>
    private const double ObstacleLookAhead = 3.0;
    /// <summary>The highest obstacle measured (higher is "too high").</summary>
    private const double ObstacleMaxMeasure = 8.0;
    /// <summary>A locked door: the fence beside it is measured this far to either side.</summary>
    private const double DoorSideOffset = 1.6;
    private const uint StrengthAttr = 1, BurdenProp = 5, CarryAugsProp = 230;

    private string _jumpModelSaid = "";

    /// <summary>The character's jump now: the buffed Jump skill and the burden (NaN when not readable).</summary>
    private JumpModel ReadJumpModel()
    {
        uint pid = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (pid == 0) return new JumpModel(0, double.NaN);
        int skill = 0;
        if (Host.HasGetObjectSkillBuffed && Host.TryGetObjectSkillLevel(pid, JumpModel.JumpSkillId, 0, out int lvl)) skill = lvl;
        else if (Host.HasGetObjectSkill && Host.TryGetObjectSkill(pid, JumpModel.JumpSkillId, out int buffed, out _)) skill = buffed;
        double burden = double.NaN;
        if (Host.HasGetObjectIntProperty && Host.HasGetObjectAttribute
            && Host.TryGetObjectIntProperty(pid, BurdenProp, out int carried)
            && Host.TryGetObjectAttribute(pid, StrengthAttr, 0, out uint str) && str > 0)
        {
            int augs = Host.TryGetObjectIntProperty(pid, CarryAugsProp, out int a) ? Math.Clamp(a, 0, 5) : 0;
            burden = carried / (150.0 * str + augs * 30.0 * str);
        }
        var m = new JumpModel(skill, burden);
        string said = m.Known ? $"Jump {m.Skill}, burden {(m.BurdenKnown ? (m.Burden * 100).ToString("F0") + "%" : "?")}: a full jump clears about {m.FullHeight - JumpModel.Clearance:F1} m" : "";
        if (said.Length > 0 && said != _jumpModelSaid) { _jumpModelSaid = said; Host.Log("[RynthNav] jump: " + said); }
        return m;
    }

    /// <summary>
    /// How high the static thing in front of (x, y) heading <paramref name="headingDeg"/> is (m above the feet):
    /// true with it; false when nothing static is there or RynthAi can't tell (no dats, older RynthAi).
    /// </summary>
    private unsafe bool ObstacleTop(double x, double y, double headingDeg, double ahead, out double top)
    {
        top = double.NaN;
        RynthAiDungeonPathApiV1* t = DungeonPathTable();
        if (t == null || t->Version < 3 || t->StructSize < 32 || t->ObstacleAhead == null) return false;
        double v;
        int r = t->ObstacleAhead(_cellId, x, y, _wz, headingDeg, ahead, ObstacleMaxMeasure, &v);
        if (r != RynthAiDungeonPathApiV1.ResultOk) return false;
        top = v;
        return true;
    }

    /// <summary>The jumps for the spot the walk is stuck at: measured when it can be.</summary>
    private void PlanJumpHere(string at)
    {
        JumpModel m = ReadJumpModel();
        if (!m.Known) return;   // no skill to go on: the default plan
        double top = ObstacleTop(_wx, _wy, _lastDriveDeg, ObstacleLookAhead, out double t) ? t : double.NaN;
        int[] plan = m.PlanFor(top, _jump.Hard, out string why);
        _jump.SetSpot(plan);
        Host.Log($"[RynthNav] walk: stuck at {at}: {why}{(plan.Length == 0 ? ": stepping round it" : "")}");
    }

    /// <summary>
    /// A locked door on the way: the fence or wall it sits in, measured to either side of it. Low enough for
    /// this character's jump: jump it (true; StepBlocked flies the jump). Else false: plan round the door.
    /// </summary>
    private bool TryJumpLockedDoor()
    {
        if (!_lockedDoorHere || !(Host.HasCommenceJump || Host.HasTapJump)) return false;
        _lockedDoorHere = false;
        double dx = _lockedDoorX - _wx, dy = _lockedDoorY - _wy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > 12) return false;
        double heading = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;
        JumpModel m = ReadJumpModel();
        if (!m.Known) return false;
        // The fence beside the door: from points beside us, square to the door.
        double px = Math.Cos(heading * Math.PI / 180.0), py = -Math.Sin(heading * Math.PI / 180.0);
        double top = double.NaN;
        foreach (int side in new[] { -1, 1 })
        {
            if (!ObstacleTop(_wx + px * DoorSideOffset * side, _wy + py * DoorSideOffset * side, heading, d + 2.0, out double t)) continue;
            top = double.IsNaN(top) ? t : Math.Max(top, t);
        }
        string at = NavCoords.Fmt(NavCoords.NsFromWorld(_wy), NavCoords.EwFromWorld(_wx));
        if (double.IsNaN(top)) { Host.Log($"[RynthNav] door: locked; the wall beside it can't be measured: planning round it"); return false; }
        int c = m.ChargeFor(top);
        if (c == 0)
        {
            Host.Log($"[RynthNav] door: locked; the wall beside it is {top:F1} m, your jump clears about {m.FullHeight - JumpModel.Clearance:F1} m: planning round it");
            return false;
        }
        _jumpDeg = heading;
        _jump.Reset();
        _jump.SetSpot(c >= 1000 ? new[] { 1000, 1000 } : new[] { c, 1000 });
        DriveTo(_jumpDeg);
        if (Host.HasCommenceJump && Host.CommenceJump()) _jump.StartCharged(NowMs);
        else { Host.TapJump(); _jump.StartTap(NowMs); }
        Host.Log($"[RynthNav] door: locked at {at}; the wall beside it is {top:F1} m: jumping it ({c} ms; Jump {m.Skill})");
        Say($"goto: the door is locked; jumping the {top:F1} m wall beside it");
        lock (_gate) _status = "jumping a locked door's wall";
        return true;
    }

    // The locked door the door walk just gave up on (world units), for TryJumpLockedDoor.
    private bool _lockedDoorHere;
    private double _lockedDoorX, _lockedDoorY;
}

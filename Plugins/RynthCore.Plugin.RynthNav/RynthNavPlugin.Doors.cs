using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// Doors, locks, jumps and drops on RynthNav's walks inside dungeons (2026-10-30). The walks (a
// route's way through a learned dungeon, the learn-portals walk) went straight at a closed door
// until the 8 s stuck clock stopped them, never jumped, and the dungeon pathfinder never planned a
// way down a ledge. Now, each tick of a dungeon walk (WalkDoor, before the walker steps):
//   - a closed door across the walk in the next 10 m (WalkDoors.AlongWalk) is used (the server
//     walks you to it and swings it open); the walk holds while it opens, its clocks waiting too;
//   - a door still closed after the server answered ("The door is locked!", or two uses that
//     didn't open it) is unlocked with what the pack has: each key or keyring once, then each
//     lockpick up to three times (a pick can fail on skill), opening it again after each;
//   - a door nothing opens is remembered (this session) and the walk is planned again round it
//     (RynthAi's dungeon pathfinder leaves its doorway out); no other way: the walk stops and says so.
// The walker jumps (running on) when it makes no progress for 2 s, twice per spot, before giving up
// at 8 s (HubWalker.Act.Jump). Planned walks may drop down a ledge or shaft up to "/rnav doors
// drops" metres (8 by default; never up), and on a walk over several levels a point is reached only
// on its own level (HubWalker.LevelOk). "/rnav doors" for the switches.
public sealed partial class RynthNavPlugin
{
    private const uint BitfieldDoor = 0x1000;
    private const uint PhysicsEthereal = 0x4;         // CPhysicsObj state: an open door is ethereal
    private const long DoorScanMs = 250;
    private const double DoorUseUnits = 6.0;          // use a door across the walk this close (along the walk)
    private const long DoorAnswerMs = 6000;           // a use with no answer this long is lost
    private const long DoorSettleMs = 1200;           // answered, and still closed this long after: it didn't open
    private const int DoorMaxUses = 3;
    private const int DoorPickTries = 3;
    private const long DoorOpenSkipMs = 30_000;       // an opened door isn't looked at again for this long
    private const long DoorBlockedSkipMs = 300_000;   // nor a door nothing opened
    private const double DropMetersDefault = 8.0;

    private enum DoorStep { None, Opening, Unlocking }
    internal enum DoorAct { None, Busy, Blocked }

    private bool _doorsEnabled = true, _unlockEnabled = true;
    private double _dropMeters = DropMetersDefault;
    private DoorStep _doorStep;
    private uint _doorId;
    private string _doorName = "";
    private long _doorSentMs, _doorAnsweredMs, _doorLastTickMs, _nextDoorScanMs;
    private int _doorSeq, _doorUses;
    private bool _doorUnlockTried;
    private List<(uint Id, string Name, WalkDoors.Unlocker Kind)>? _doorUnlockers;
    private int _doorUnlockIdx, _doorPickUses;
    private long _doorLockedSaidMs = long.MinValue;     // NowMs of the last "is locked" line (any thread: Interlocked)
    private readonly Dictionary<uint, long> _doorSkipUntil = new();
    private readonly List<(uint Lb, double X, double Y, double Z)> _lockedDoors = new();
    private int _lockedDoorVersion;
    private bool _doorNoStateSaid;

    private void InitDoors()
    {
        _doorsEnabled = NavDataConfig.ReadSwitch("doors", true);
        _unlockEnabled = NavDataConfig.ReadSwitch("unlockDoors", true);
        _dropMeters = Math.Clamp(NavDataConfig.ReadNumber("dropMeters", DropMetersDefault), 0, 40);
    }

    /// <summary>The locked doors found in dungeon <paramref name="lb"/> (landblock-local), for the pathfinder to go round.</summary>
    private List<(double X, double Y, double Z)> LockedDoorSpots(uint lb)
    {
        var list = new List<(double, double, double)>();
        foreach (var d in _lockedDoors) if (d.Lb == (lb & 0xFFFF)) list.Add((d.X, d.Y, d.Z));
        return list;
    }

    /// <summary>A chat line: the server's "The door is locked!" (any thread).</summary>
    private void NoteDoorText(string? text)
    {
        if (WalkDoors.IsLockedText(text)) System.Threading.Interlocked.Exchange(ref _doorLockedSaidMs, NowMs);
    }

    private bool LockedSaidSince(long ms) => System.Threading.Interlocked.Read(ref _doorLockedSaidMs) >= ms;

    /// <summary>Forgets the door being worked (a new walk, the goto ending).</summary>
    private void ResetDoor()
    {
        _doorStep = DoorStep.None;
        _doorId = 0; _doorName = "";
        _doorUses = 0; _doorUnlockTried = false;
        _doorUnlockers = null; _doorUnlockIdx = 0; _doorPickUses = 0;
        _doorLastTickMs = 0;
    }

    /// <summary>
    /// Doors on a dungeon walk, before the walker steps: None (walk on), Busy (a door is being
    /// opened or unlocked: the walk holds), Blocked (a door across the walk nothing opens:
    /// <paramref name="why"/>; it is remembered, so a walk planned again goes round it).
    /// </summary>
    private DoorAct WalkDoor(HubWalker w, long now, out string why)
    {
        var ahead = new List<TownNetPoint>();
        foreach (var (_, p) in w.Ahead()) ahead.Add(p);
        return WalkDoor(ahead, w.Levels, (n, held) => w.Hold(n, held), now, out why);
    }

    /// <summary>
    /// The same for any walk (2026-11-01: the goto's own walk over land and through towns, not only the
    /// dungeon walks): <paramref name="ahead"/> are the walk's points still ahead in this landblock's
    /// local coordinates, <paramref name="hold"/> pauses the walk's clocks while a door is worked.
    /// </summary>
    private DoorAct WalkDoor(List<TownNetPoint> ahead, bool levels, Action<long, long> hold, long now, out string why)
    {
        why = "";
        if (!_doorsEnabled || !Host.HasUseObject || !Host.HasGetLiveObjectIds) return DoorAct.None;
        if (!Host.HasGetObjectState && !Host.HasGetObjectMotionOn)
        {
            if (!_doorNoStateSaid) { _doorNoStateSaid = true; Host.Log("[RynthNav] doors: this RynthCore engine can't tell an open door from a closed one: walks don't open doors"); }
            return DoorAct.None;
        }
        long held = _doorLastTickMs == 0 ? 0 : Math.Max(0, now - _doorLastTickMs);
        _doorLastTickMs = now;

        if (_doorStep == DoorStep.None)
        {
            if (now < _nextDoorScanMs) { _doorLastTickMs = 0; return DoorAct.None; }
            _nextDoorScanMs = now + DoorScanMs;
            uint id = ClosedDoorAhead(ahead, levels, now, out double along, out string name);
            if (id == 0 || along > DoorUseUnits) { _doorLastTickMs = 0; return DoorAct.None; }
            _doorId = id; _doorName = name;
            _doorUses = 0; _doorUnlockTried = false; _doorUnlockers = null; _doorUnlockIdx = 0; _doorPickUses = 0;
            Host.Log($"[RynthNav] door: '{name}' (0x{id:X8}) is closed across the walk {along:F1} m ahead: opening it");
            lock (_gate) _status = $"opening the {name}";
            if (!SendDoorUse(now)) { ResetDoor(); return DoorAct.None; }
            hold(now, 0);
            return DoorAct.Busy;
        }

        hold(now, held);
        AutoRun.Halt();
        if (DoorOpen(_doorId))
        {
            Host.Log($"[RynthNav] door: '{_doorName}' is open{(_doorUnlockTried ? " (unlocked)" : "")}");
            if (_doorUnlockTried) Say($"unlocked and opened the {_doorName}.");
            _doorSkipUntil[_doorId] = now + DoorOpenSkipMs;
            ResetDoor();
            return DoorAct.None;
        }

        bool answered = DoorAnswered(out uint err);
        if (answered && _doorAnsweredMs == 0)
        {
            _doorAnsweredMs = now;
            if (err != 0) Host.Log($"[RynthNav] door: the server answered the {(_doorStep == DoorStep.Unlocking ? "unlock" : "use")} with error 0x{err:X}");
        }

        if (_doorStep == DoorStep.Unlocking)
        {
            // The key or pick was answered (or lost): use the door again.
            if ((answered && now - _doorAnsweredMs >= 300) || now - _doorSentMs > DoorAnswerMs)
            {
                _doorStep = DoorStep.Opening;
                _doorUses = 0;
                if (!SendDoorUse(now)) return Blocked(now, "the door use wouldn't go out", false, out why);
            }
            return DoorAct.Busy;
        }

        // Opening: answered and still closed a moment later, or no answer at all.
        bool settled = answered && now - _doorAnsweredMs >= DoorSettleMs;
        bool lost = !answered && now - _doorSentMs > DoorAnswerMs;
        if (!settled && !lost) return DoorAct.Busy;
        bool locked = LockedSaidSince(_doorSentMs) || (settled && _doorUses >= 2);
        if (locked || (_doorUnlockTried && settled))
        {
            if (!_unlockEnabled)
                return Blocked(now, $"the {_doorName} is locked (unlocking is off: /rnav doors unlock on)", true, out why);
            if (!SendUnlock(now, out string none))
                return Blocked(now, $"the {_doorName} is locked and {none}", true, out why);
            return DoorAct.Busy;
        }
        if (_doorUses >= DoorMaxUses)
            return Blocked(now, $"the {_doorName} didn't open after {_doorUses} uses", false, out why);
        Host.Log($"[RynthNav] door: '{_doorName}' still closed after use {_doorUses}{(lost ? " (no answer)" : "")}: using it again");
        if (!SendDoorUse(now)) return Blocked(now, "the door use wouldn't go out", false, out why);
        return DoorAct.Busy;
    }

    private DoorAct Blocked(long now, string text, bool locked, out string why)
    {
        why = text;
        Host.Log($"[RynthNav] door: {text}");
        _doorSkipUntil[_doorId] = now + DoorBlockedSkipMs;
        if (_doorId != 0 && Host.TryGetObjectPosition(_doorId, out uint cell, out float x, out float y, out float z) && cell != 0)
        {
            _lockedDoors.Add(((cell >> 16) & 0xFFFF, x, y, z));
            _lockedDoorVersion++;
        }
        ResetDoor();
        return DoorAct.Blocked;
    }

    private bool SendDoorUse(long now)
    {
        _doorSeq = Host.HasUseDoneSeq ? Host.GetUseDoneSeq() : 0;
        bool sent = Host.UseFor(_doorId, "Goto", $"open the {_doorName} across the dungeon walk");
        _doorStep = DoorStep.Opening;
        _doorSentMs = now; _doorAnsweredMs = 0;
        if (sent) _doorUses++;
        return sent;
    }

    /// <summary>The next key or lockpick from the pack on the door; false (<paramref name="why"/>) when none is left.</summary>
    private bool SendUnlock(long now, out string why)
    {
        why = "";
        _doorUnlockers ??= WalkDoors.TryOrder(PackItems());
        if (_doorUnlockers.Count == 0) { why = "there's no key or lockpick in your pack"; return false; }
        while (_doorUnlockIdx < _doorUnlockers.Count)
        {
            var u = _doorUnlockers[_doorUnlockIdx];
            bool pick = u.Kind == WalkDoors.Unlocker.Lockpick;
            if (_doorUnlockTried && (!pick || _doorPickUses >= DoorPickTries)) { _doorUnlockIdx++; _doorPickUses = 0; _doorUnlockTried = false; continue; }
            if (!Host.TryGetObjectName(u.Id, out _)) { _doorUnlockIdx++; _doorPickUses = 0; continue; }   // used up
            _doorSeq = Host.HasUseDoneSeq ? Host.GetUseDoneSeq() : 0;
            if (!Host.UseOnFor(u.Id, _doorId, "Goto", $"unlock the {_doorName} on the dungeon walk")) { why = "the unlock wouldn't go out"; return false; }
            if (pick) _doorPickUses++;
            _doorUnlockTried = true;
            _doorStep = DoorStep.Unlocking;
            _doorSentMs = now; _doorAnsweredMs = 0;
            Host.Log($"[RynthNav] door: '{_doorName}' is locked: trying {u.Name}{(pick ? $" (pick {_doorPickUses} of {DoorPickTries})" : "")}");
            lock (_gate) _status = $"unlocking the {_doorName} with {u.Name}";
            return true;
        }
        why = $"none of your {_doorUnlockers.Count} key(s)/lockpick(s) opened it";
        return false;
    }

    private bool DoorAnswered(out uint err)
    {
        err = 0;
        if (!Host.HasUseDoneSeq) return NowMs - _doorSentMs > 2000;
        int seq = Host.GetUseDoneSeq();
        if (seq == _doorSeq) return false;
        if (Host.HasGetLastUseDone && Host.TryGetLastUseDone(out int ls, out uint le) && ls == _doorSeq + 1) err = le;
        return true;
    }

    private bool DoorOpen(uint id)
    {
        if (Host.HasGetObjectState && Host.TryGetObjectState(id, out uint st)) return (st & PhysicsEthereal) != 0;
        if (Host.HasGetObjectMotionOn && Host.TryGetObjectMotionOn(id, out bool on)) return on;
        return false;
    }

    private static bool LooksLikeDoorName(string n) =>
        n.Contains("door", StringComparison.OrdinalIgnoreCase) || n.Contains("gate", StringComparison.OrdinalIgnoreCase)
        || n.Contains("hatch", StringComparison.OrdinalIgnoreCase) || n.Contains("portcullis", StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest closed door standing across the walk ahead (0: none), and how far along the walk it is.</summary>
    private uint ClosedDoorAhead(List<TownNetPoint> ahead, bool levels, long now, out double bestAlong, out string bestName)
    {
        bestAlong = double.MaxValue; bestName = "";
        uint best = 0;
        if (ahead.Count == 0) return 0;
        uint lb = CurrentLandblock;
        double x = LocalX, y = LocalY;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (id >= 0x80000000u) continue;   // doors are static objects
            if (_doorSkipUntil.TryGetValue(id, out long until) && now < until) continue;
            if (!Host.TryGetObjectPosition(id, out uint cell, out float dxf, out float dyf, out float dz)) continue;
            double dx = dxf, dy = dyf;
            if (!SameFrame(cell, lb, ref dx, ref dy)) continue;
            if (Math.Abs(dx - x) > WalkDoors.LookAheadUnits + 2 || Math.Abs(dy - y) > WalkDoors.LookAheadUnits + 2) continue;
            Host.TryGetObjectName(id, out string name);
            name ??= "";
            bool door = Host.HasGetObjectBitfield
                ? Host.TryGetObjectBitfield(id, out uint bf) && (bf & BitfieldDoor) != 0
                : LooksLikeDoorName(name);
            if (!door || DoorOpen(id)) continue;
            double along = WalkDoors.AlongWalk(x, y, _wz, ahead, levels, dx, dy, dz);
            if (along < 0 || along >= bestAlong) continue;
            bestAlong = along; best = id; bestName = name.Length > 0 ? name : "door";
        }
        return best;
    }

    /// <summary>
    /// A position in <paramref name="cell"/>'s landblock as coordinates local to landblock <paramref name="lb"/>:
    /// the same landblock as it is; on the map, a neighbouring landblock shifted by 192 a block (a door
    /// just over the line). False: a dungeon elsewhere, or too far.
    /// </summary>
    private bool SameFrame(uint cell, uint lb, ref double x, ref double y)
    {
        uint olb = (cell >> 16) & 0xFFFF;
        if (olb == lb) return true;
        if (!IsOnMap(_cellId)) return false;   // a dungeon has no neighbours
        int ox = (int)(olb >> 8), oy = (int)(olb & 0xFF), cx = (int)(lb >> 8), cy = (int)(lb & 0xFF);
        if (Math.Abs(ox - cx) > 1 || Math.Abs(oy - cy) > 1) return false;
        x += (ox - cx) * 192.0;
        y += (oy - cy) * 192.0;
        return true;
    }

    /// <summary>
    /// Doors on the goto's own walk (the navmesh path over land, through towns and buildings), each tick
    /// before it steers: the path's points still ahead, in this landblock's local coordinates.
    /// </summary>
    private DoorAct GotoDoor(long now, out string why)
    {
        why = "";
        if (!_doorsEnabled) return DoorAct.None;
        List<(double ew, double ns)>? path; int idx;
        lock (_gotoGate) { path = _gotoPath; idx = _gotoIdx; }
        if (path == null || idx >= path.Count) { if (_doorStep == DoorStep.None) return DoorAct.None; path ??= new(); }
        double ox = ((_cellId >> 24) & 0xFF) * 192.0, oy = ((_cellId >> 16) & 0xFF) * 192.0;
        var ahead = new List<TownNetPoint>(Math.Max(0, path.Count - idx));
        for (int i = idx; i < path.Count; i++)
            ahead.Add(new TownNetPoint { X = path[i].ew - ox, Y = path[i].ns - oy, Z = double.NaN });
        return WalkDoor(ahead, false, (n, held) => { _bestTick = _legTicks; _stuckWinMs = 0; }, now, out why);
    }

    /// <summary>
    /// Blocked with no door on the path ahead (the mesh may go round a door it was baked without): a closed
    /// door within reach in front of us, used before jumping or stepping round (2026-11-01).
    /// </summary>
    private bool OpenDoorInFront()
    {
        if (!_doorsEnabled || !Host.HasUseObject || !Host.HasGetLiveObjectIds) return false;
        if (!Host.HasGetObjectState && !Host.HasGetObjectMotionOn) return false;
        long now = NowMs;
        uint lb = CurrentLandblock;
        double x = LocalX, y = LocalY;
        double hx = Math.Sin(_lastDriveDeg * Math.PI / 180.0), hy = Math.Cos(_lastDriveDeg * Math.PI / 180.0);
        uint best = 0; double bestD = double.MaxValue; string bestName = "";
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (id >= 0x80000000u) continue;
            if (_doorSkipUntil.TryGetValue(id, out long until) && now < until) continue;
            if (!Host.TryGetObjectPosition(id, out uint cell, out float dxf, out float dyf, out float dz)) continue;
            double dx = dxf, dy = dyf;
            if (!SameFrame(cell, lb, ref dx, ref dy)) continue;
            if (!WalkDoors.InFront(x, y, _wz, hx, hy, dx, dy, dz, out double d) || d >= bestD) continue;
            Host.TryGetObjectName(id, out string name);
            name ??= "";
            bool door = Host.HasGetObjectBitfield
                ? Host.TryGetObjectBitfield(id, out uint bf) && (bf & BitfieldDoor) != 0
                : LooksLikeDoorName(name);
            if (!door || DoorOpen(id)) continue;
            best = id; bestD = d; bestName = name.Length > 0 ? name : "door";
        }
        if (best == 0) return false;
        _doorId = best; _doorName = bestName;
        _doorUses = 0; _doorUnlockTried = false; _doorUnlockers = null; _doorUnlockIdx = 0; _doorPickUses = 0;
        Host.Log($"[RynthNav] door: blocked by '{bestName}' (0x{best:X8}) {bestD:F1} m in front: opening it");
        lock (_gate) _status = $"opening the {bestName}";
        if (!SendDoorUse(now)) { ResetDoor(); return false; }
        _doorLastTickMs = now;
        return true;
    }

    /// <summary>The items in your pack (main pack and side packs), by id and name.</summary>
    private List<(uint Id, string Name)> PackItems()
    {
        var list = new List<(uint, string)>();
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me == 0 || !Host.HasGetObjectOwnershipInfo) return list;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _) || wielder == me) continue;
            bool mine = container == me
                || (container != 0 && Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == me);
            if (!mine) continue;
            Host.TryGetObjectName(id, out string name);
            if (!string.IsNullOrEmpty(name)) list.Add((id, name));
        }
        return list;
    }

    /// <summary>A dungeon walk's jump (HubWalker.Act.Jump): on at the heading, and a jump.</summary>
    private void WalkJump(double heading, HubWalker w)
    {
        DriveTo(heading);
        bool ok = Host.HasTapJump && Host.TapJump();
        Host.Log($"[RynthNav] walk: no progress, jumping{(ok ? "" : " (this engine can't jump)")} (jump {w.JumpsTotal} on this walk)");
    }

    /// <summary>/rnav doors [on|off | unlock on|off | drops &lt;m&gt;|off | forget].</summary>
    private void CmdDoors(string rest)
    {
        string[] a = rest.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = a.Length > 0 ? a[0] : "";
        string arg = a.Length > 1 ? a[1] : "";
        switch (sub)
        {
            case "on":
            case "off":
                _doorsEnabled = sub == "on";
                NavDataConfig.WriteSwitch("doors", _doorsEnabled);
                Say($"doors on walks {(_doorsEnabled ? "ON: closed doors across the walk, or in the way, are opened" : "OFF")}");
                return;
            case "unlock":
                if (arg is not ("on" or "off")) { SayFail("doors unlock on|off — use keys and lockpicks from your pack on a locked door"); return; }
                _unlockEnabled = arg == "on";
                NavDataConfig.WriteSwitch("unlockDoors", _unlockEnabled);
                Say($"unlocking doors {(_unlockEnabled ? "ON: keys first, then lockpicks" : "OFF: a locked door is gone round, or the walk stops")}");
                return;
            case "drops":
            case "drop":
                if (arg == "off") _dropMeters = 0;
                else if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m >= 0 && m <= 40) _dropMeters = Math.Round(m, 1);
                else { SayFail($"doors drops <metres 0-40>|off — how far a dungeon walk may drop down a ledge (now {DropText()})"); return; }
                NavDataConfig.WriteNumber("dropMeters", _dropMeters);
                _dunPathCache.Clear();
                Say($"dungeon walks may drop {DropText()} (planned walks are worked out again)");
                return;
            case "forget":
                int n = _lockedDoors.Count;
                _lockedDoors.Clear(); _doorSkipUntil.Clear(); _lockedDoorVersion++;
                Say($"forgot {n} locked door(s): walks may try them again");
                return;
            case "":
                Say($"doors: {(_doorsEnabled ? "opened on walks (dungeons, towns, buildings)" : "OFF")}; unlock: {(_unlockEnabled ? "keys, then lockpicks" : "OFF")}; drops: {DropText()}; " +
                    $"{_lockedDoors.Count} locked door(s) gone round this session; jumps: when a walk stops making progress");
                return;
            default:
                SayFail("doors [on|off] | unlock on|off | drops <m>|off | forget");
                return;
        }
    }

    private string DropText() => _dropMeters > 0 ? $"down ledges up to {_dropMeters:0.#} m (never up)" : "OFF (no drops)";
}

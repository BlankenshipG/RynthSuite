using System;

namespace RynthCore.Plugin.RynthNav;

// Walks inside dungeons (2026-10-29): dungeons have no navmesh, so RynthNav keeps the way you walked
// in one (DungeonTrail: from the landing, loops cut out). Taking a portal there keeps that walk with
// the portal it reached (RynthNavPlugin.CustomPortals.cs), and TownNet.Merge turns this server's
// walks into a hub the route planner and the walker use like the Town Network. Recorded whoever does
// the walking: you, a goto, or learning portals. Not inside the Town Network (townnet.json has it).
// Tick thread.
public sealed partial class RynthNavPlugin
{
    private readonly DungeonTrail _trail = new();

    // The nearest live portal a moment ago (within UsedPortalUnits): when the client moves you before
    // portal space, the portal's object may be gone by the time the jump is seen.
    private int _nearPortalTick;
    private bool _nearPortalSeen;
    private long _nearPortalMs;
    private string _nearPortalName = "";
    private uint _nearPortalWcid, _nearPortalCell;
    private double _nearPortalWx, _nearPortalWy, _nearPortalWz;

    private void TickNearPortal()
    {
        if (!_hasPose || !Host.HasGetLiveObjectIds || (++_nearPortalTick % 8) != 0) return;
        if (_trkPortaling || (Host.HasIsPortaling && Host.IsPortaling())) return;
        double best = UsedPortalUnits * UsedPortalUnits;
        bool found = false;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!LivePortalAt(id, out uint pc, out double px, out double py, out double pz, out string name)) continue;
            double d2 = (px - _wx) * (px - _wx) + (py - _wy) * (py - _wy);
            if (d2 >= best || Math.Abs(pz - _wz) > 4.0) continue;
            best = d2; found = true;
            Host.TryGetObjectWcid(id, out uint wcid);
            _nearPortalName = name; _nearPortalWcid = wcid; _nearPortalCell = pc;
            _nearPortalWx = px; _nearPortalWy = py; _nearPortalWz = pz;
        }
        if (found) { _nearPortalSeen = true; _nearPortalMs = NowMs; }
    }

    /// <summary>The portal seen beside (wx, wy) within the last moment, for NoteUsedPortal when the live one is gone.</summary>
    private bool RecentNearPortal(double wx, double wy, out uint cell, out double px, out double py, out double pz, out string name, out uint wcid)
    {
        cell = _nearPortalCell; px = _nearPortalWx; py = _nearPortalWy; pz = _nearPortalWz; name = _nearPortalName; wcid = _nearPortalWcid;
        return _nearPortalSeen && NowMs - _nearPortalMs < 2500
            && (px - wx) * (px - wx) + (py - wy) * (py - wy) < UsedPortalUnits * UsedPortalUnits;
    }

    /// <summary>One tick of the trail: started at each landing in a dungeon, extended as you move, dropped on the map.</summary>
    private void TickDungeonTrail(bool teleported)
    {
        if (!_hasPose || (_cellId >> 16) == 0) return;
        if (_trkPortaling || (Host.HasIsPortaling && Host.IsPortaling())) return;   // NoteUsedPortal took what it needed
        uint lb = CurrentLandblock;
        bool dungeon = !IsOnMap(_cellId) && (_cellId & 0xFFFF) >= 0x100 && _baseTownNet?.Contains(lb) != true;
        if (!dungeon) { if (_trail.Count > 0) _trail.Clear(); return; }
        if (teleported || _trail.Count == 0 || _trail.Landblock != lb)
        {
            bool fromLanding = teleported || (LastTeleportMs > 0 && NowMs - LastTeleportMs < 15000);
            _trail.Start(_cellId, LocalX, LocalY, _wz, fromLanding);
            return;
        }
        _trail.Add(LocalX, LocalY, _wz);
    }
}

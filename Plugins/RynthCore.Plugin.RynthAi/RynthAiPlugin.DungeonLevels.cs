using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// The Dungeon Patrol over a dungeon's levels (2026-11-08; DungeonPathfinder.Levels.cs). The patrol walks the level it
// is built on (the cells walkable from there). Once round it goes down the drop (a pit, a shaft, a ledge: walked to the
// edge and jumped) to the biggest part of the dungeon not patrolled yet and patrols there; and a character who ends up
// on another level (a chase into a pit: 11-08 10:41, dungeon 0x02D4, 17 m down) has the patrol rebuilt where it is,
// instead of steering at waypoints overhead. The levels patrolled are remembered until the patrol is started anew or
// the dungeon changes. Pump thread.
public sealed partial class RynthAiPlugin
{
    private HashSet<uint> _dunPatrolLevel = new();          // the cells the current patrol's level has
    private readonly HashSet<uint> _dunPatrolSeen = new();  // every level patrolled in this dungeon
    private uint _dunLevelsLb = uint.MaxValue;
    private int _dunLastIdx = -1;
    private bool _dunDescending;
    private HashSet<uint> _dunDescentBelow = new();
    private long _dunDescentUntil, _dunLevelsNextAt;
    private bool _dunNoDescentSaid;
    private const long DunDescentMaxMs = 120_000, DunLevelsEveryMs = 1000;

    /// <summary>BuildDunPatrol's part: the level the new patrol walks, added to the ones patrolled.</summary>
    private void NotePatrolLevel(Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazards, uint landblock, bool fresh)
    {
        if (fresh || _dunLevelsLb != landblock) { _dunPatrolSeen.Clear(); _dunNoDescentSaid = false; }
        _dunLevelsLb = landblock;
        _dunPatrolLevel = DungeonPathfinder.WalkableFrom(graph, startCell, hazards);
        _dunPatrolSeen.UnionWith(_dunPatrolLevel);
        _dunLastIdx = -1;
        _dunDescending = false;
    }

    /// <summary>The descent's route is the patrol's for the while (TickDunPatrolHazardReroute doesn't stand down for it).</summary>
    private bool DunPatrolDescending => _dunDescending;

    /// <summary>Each tick while the patrol is on: the level it is on, a lap done, a descent landed.</summary>
    private void TickDunPatrolLevels()
    {
        if (!_dunPatrolActive) return;
        var settings = _dashboard?.Settings;
        if (settings == null || !settings.IsMacroRunning) return;
        long now = Environment.TickCount64;
        if (now < _dunLevelsNextAt) return;
        _dunLevelsNextAt = now + DunLevelsEveryMs;
        if (!Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return;
        if ((cell >> 16) != _dunLevelsLb || (cell & 0xFFFF) < 0x0100) return;
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return;
        var graph = DungeonPathfinder.GetGraph(_dunLevelsLb, cellDat);
        if (!graph.ContainsKey(cell)) return;

        if (_dunDescending)
        {
            // Landed (or the walk ended or ran out of time): the patrol goes on from here.
            bool landed = _dunDescentBelow.Contains(cell);
            if (landed || !settings.EnableNavigation || now > _dunDescentUntil)
            {
                _dunDescending = false;
                BuildDunPatrol(isRebuild: true, levelNote: landed ? "down a level" : "the way down ended here");
            }
            return;
        }
        if (!settings.EnableNavigation) return;

        // On another level than the patrol's (fell into a pit, chased something down a drop): patrol this one.
        if (_dunPatrolLevel.Count > 0 && !_dunPatrolLevel.Contains(cell))
        {
            HashSet<uint> here = DungeonPathfinder.WalkableFrom(graph, cell, _objectCache?.GetHazardCells());
            if (!here.Overlaps(_dunPatrolLevel))
            {
                Log($"[RynthAi] DunNav-Patrol: on another level (cell {cell:X8}, {here.Count} cells, none of the patrol's): rebuilding here.");
                BuildDunPatrol(isRebuild: true, levelNote: "on a lower level (a drop or a pit)");
                return;
            }
        }

        // A lap done (the index came round to the start): down to a level not patrolled yet, if a drop leads to one.
        int idx = settings.ActiveNavIndex;
        int n = settings.CurrentRoute?.Points.Count ?? 0;
        bool lap = _dunLastIdx >= 0 && n > 2 && idx < _dunLastIdx && _dunLastIdx >= n - 2 && idx <= 1;
        _dunLastIdx = idx;
        if (lap) TryPatrolDescend(settings, graph, cell);
    }

    private void TryPatrolDescend(LegacyUiSettings settings, Dictionary<uint, DungeonNavNode> graph, uint cell)
    {
        var hazards = _objectCache?.GetHazardCells();
        List<uint>? path = DungeonPathfinder.DescentPath(graph, cell, _dunPatrolSeen, hazards, out HashSet<uint> below);
        if (path == null)
        {
            if (!_dunNoDescentSaid) Log("[RynthAi] DunNav-Patrol: a lap done; no drop leads to a level not patrolled yet: patrolling on here.");
            _dunNoDescentSaid = true;
            return;
        }
        if (!Host.TryGetPlayerPose(out _, out _, out _, out float pz, out _, out _, out _, out _)) return;
        if (!NavCoordinateHelper.TryGetNavCoords(Host, out double pNs, out double pEw)) return;
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null) return;
        var geo = DungeonPathfinder.GetGeometry(_dunLevelsLb, cellDat, _raycast?.GeometryLoader?.DungeonLOS);
        DungeonNavNode bottom = graph[path[^1]];
        NavRouteParser route = DungeonPathfinder.BuildDropRoute(path, graph, geo, hazards, pNs, pEw, pz, bottom.NS, bottom.EW, bottom.Z, out int drops);
        if (route.Points.Count == 0 || drops == 0) return;
        _dunDescending = true;
        _dunDescentBelow = below;
        _dunDescentUntil = Environment.TickCount64 + DunDescentMaxMs;
        _dunPatrolRoute = route;
        settings.CurrentNavPath = string.Empty;
        settings.CurrentRoute = route;
        settings.ActiveNavIndex = 0;
        settings.EnableNavigation = true;
        int fresh = 0;
        foreach (uint c in below) if (!_dunPatrolSeen.Contains(c)) fresh++;
        ChatLine($"[RynthAi] DunNav-Patrol: this level is patrolled; going down a drop ({graph[path[^2]].Z - bottom.Z:F0} m) to {fresh} cells not patrolled yet.");
    }

    // ── dunnav (the quest's dungeon goto) on another level ──
    private NavRouteParser? _dunNavRoute;
    private double _dunNavDestNs = double.NaN, _dunNavDestEw;
    private long _dunNavReplanAt;
    private const long DunNavReplanMs = 20_000;

    private void NoteDunNavRoute(NavRouteParser route, double ns, double ew)
    {
        _dunNavRoute = route;
        _dunNavDestNs = ns;
        _dunNavDestEw = ew;
    }

    /// <summary>
    /// NavigationEngine: the waypoint is a level above or below with no walk to it (a fall into a pit). The patrol is
    /// rebuilt on this level; a dunnav walk is planned again from here (drops down taken), at most every 20 s.
    /// </summary>
    private void OnWaypointOtherLevel(int idx)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) return;
        long at = Environment.TickCount64;
        if (_dunPatrolActive)
        {
            if (at < _dunNavReplanAt) return;
            _dunNavReplanAt = at + DunNavReplanMs;
            if (!_dunDescending) BuildDunPatrol(isRebuild: true, levelNote: "the patrol's waypoints are on another level");
            return;
        }
        if (_dunNavRoute == null || !ReferenceEquals(settings.CurrentRoute, _dunNavRoute) || double.IsNaN(_dunNavDestNs)) return;
        long now = Environment.TickCount64;
        if (now < _dunNavReplanAt) return;
        _dunNavReplanAt = now + DunNavReplanMs;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Log($"[RynthAi] DunNav: waypoint {idx + 1} is on another level: planning again from here.");
        HandleDungeonNavCommand(new[] { "/ra", "dunnav", _dunNavDestNs.ToString("0.#####", inv), _dunNavDestEw.ToString("0.#####", inv) });
    }
}

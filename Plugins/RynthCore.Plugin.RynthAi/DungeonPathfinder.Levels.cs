// Dungeon levels (2026-11-08, Silentkelpie: "the route is not taking into account for elevation or pits to jump in
// it should build in auto jumps to go down in levels and if one gets stuck in a pit ... it should use backtrack to
// get unstuck"). A drop (a pit, a shaft, a ledge: IsDropEdge) is a one-way step down. The dungeon map's own routes
// (dunnav, the quest's dungeon goto) take drops of up to PlannedMaxDropZ, with a Jump point at the edge; the patrol
// walks one level (the cells walkable from where it starts) and, once round, goes down the drop to the biggest part
// of the dungeon it hasn't patrolled (DescentPath) and patrols there. Log of the run that asked for it: 11-08 10:39,
// dungeon 0x02D4, the patrol looped 43 of the dungeon's cells for ten minutes; a chase into the pit at 10:41:50
// (17 m down) left nav steering at waypoints overhead.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal static partial class DungeonPathfinder
{
    /// <summary>How far down a planned drop may go (world units): a floor or two; farther costs too much health.</summary>
    internal const double PlannedMaxDropZ = 25.0;

    /// <summary>Two heights this far apart (world units) are different levels (a waypoint up there isn't reached down here).</summary>
    internal const double LevelApartZ = 8.0;

    /// <summary>The cells walkable from <paramref name="start"/>: no drops, no hazard cells (the start's level).</summary>
    internal static HashSet<uint> WalkableFrom(Dictionary<uint, DungeonNavNode> graph, uint start, IReadOnlySet<uint>? hazards = null)
    {
        var seen = new HashSet<uint>();
        if (!graph.ContainsKey(start)) return seen;
        var q = new Queue<uint>();
        seen.Add(start);
        q.Enqueue(start);
        while (q.Count > 0)
        {
            uint c = q.Dequeue();
            DungeonNavNode n = graph[c];
            foreach (uint nb in n.Neighbors)
            {
                if (seen.Contains(nb) || !graph.TryGetValue(nb, out var o) || IsDropEdge(n, o)) continue;
                if (hazards != null && hazards.Contains(nb)) continue;
                seen.Add(nb);
                q.Enqueue(nb);
            }
        }
        return seen;
    }

    /// <summary>
    /// The way down to the most of the dungeon not yet patrolled: from <paramref name="start"/> over its level to the
    /// edge of a drop (down by at most <see cref="PlannedMaxDropZ"/>) whose bottom's level has the most cells outside
    /// <paramref name="patrolled"/> (the nearer drop on a tie). The cell path (start ... edge, bottom) and that level's
    /// cells; null when every drop from here leads somewhere patrolled, or there are none.
    /// </summary>
    internal static List<uint>? DescentPath(Dictionary<uint, DungeonNavNode> graph, uint start, IReadOnlySet<uint> patrolled,
        IReadOnlySet<uint>? hazards, out HashSet<uint> below)
    {
        below = new HashSet<uint>();
        HashSet<uint> level = WalkableFrom(graph, start, hazards);
        if (level.Count == 0) return null;
        uint bestEdge = 0, bestBottom = 0;
        int bestNew = 0;
        double bestDist = double.MaxValue;
        var levelOf = new Dictionary<uint, HashSet<uint>>();
        DungeonNavNode s = graph[start];
        foreach (uint c in level)
        {
            DungeonNavNode a = graph[c];
            foreach (uint nb in a.Neighbors)
            {
                if (level.Contains(nb) || !graph.TryGetValue(nb, out var b) || !IsDropEdge(a, b)) continue;
                if (!DropAllowed(a, b, PlannedMaxDropZ) || (hazards != null && hazards.Contains(nb))) continue;
                if (!levelOf.TryGetValue(nb, out var lv)) { lv = WalkableFrom(graph, nb, hazards); levelOf[nb] = lv; }
                int fresh = 0;
                foreach (uint x in lv) if (!patrolled.Contains(x) && !level.Contains(x)) fresh++;
                if (fresh == 0) continue;
                double d = H(s, a);
                if (fresh > bestNew || (fresh == bestNew && d < bestDist))
                {
                    bestNew = fresh; bestDist = d; bestEdge = c; bestBottom = nb;
                }
            }
        }
        if (bestEdge == 0) return null;
        List<uint> path = FindPath(start, bestEdge, graph, hazards);
        if (path.Count == 0) return null;
        path.Add(bestBottom);
        below = levelOf[bestBottom];
        return path;
    }

    /// <summary>
    /// A cell path walked with its drops jumped (a Once route): the walk to each drop's edge (the map's points), a Jump
    /// point there facing the way down, then on from where you land; the destination last.
    /// </summary>
    internal static NavRouteParser BuildDropRoute(List<uint> cellPath, Dictionary<uint, DungeonNavNode> graph,
        DungeonGeometry? geo, IReadOnlySet<uint>? hazards,
        double startNS, double startEW, double startZ, double destNS, double destEW, double destZ, out int drops)
    {
        drops = 0;
        var route = new NavRouteParser { RouteType = NavRouteType.Once };
        double sNs = startNS, sEw = startEW, sZ = startZ;
        int runStart = 0;
        for (int i = 0; i < cellPath.Count; i++)
        {
            bool last = i == cellPath.Count - 1;
            bool dropNext = !last && graph.TryGetValue(cellPath[i], out var na) && graph.TryGetValue(cellPath[i + 1], out var nb) && IsDropEdge(na, nb);
            if (!last && !dropNext) continue;
            var run = cellPath.GetRange(runStart, i - runStart + 1);
            if (last)
            {
                NavRouteParser leg = BuildNavRoute(run, graph, destNS, destEW, geo, hazards, sNs, sEw, sZ, destZ);
                route.Points.AddRange(leg.Points);
                break;
            }
            DungeonNavNode a = graph[cellPath[i]], b = graph[cellPath[i + 1]];
            DropEdgePoints(geo, a, b, out double ex, out double ey, out double ez, out double lx, out double ly, out double lz);
            NavRouteParser walk = BuildNavRoute(run, graph, NsOf(ey), EwOf(ex), geo, hazards, sNs, sEw, sZ, ez);
            // The walk's last point is the edge itself: it becomes the jump.
            if (walk.Points.Count > 0) walk.Points.RemoveAt(walk.Points.Count - 1);
            route.Points.AddRange(walk.Points);
            double heading = Math.Atan2(EwOf(lx) - EwOf(ex), NsOf(ly) - NsOf(ey)) * (180.0 / Math.PI);
            route.Points.Add(new NavPoint
            {
                Type = NavPointType.Jump, NS = NsOf(ey), EW = EwOf(ex), Z = ez / 240.0,
                JumpHeading = (heading + 360.0) % 360.0, JumpShift = false, JumpMs = 0,
            });
            route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = NsOf(ly), EW = EwOf(lx), Z = lz / 240.0 });
            drops++;
            sNs = NsOf(ly); sEw = EwOf(lx); sZ = lz;
            runStart = i + 1;
        }
        return route;
    }
}

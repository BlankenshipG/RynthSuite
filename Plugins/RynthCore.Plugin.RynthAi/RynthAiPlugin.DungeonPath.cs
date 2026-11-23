using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The "RynthAi.DungeonPath" interface's work (DungeonPathInterface.cs, 2026-10-29): a walk inside a
// dungeon between two points, as the Dungeon Patrol and /ra dunnav find it (the cell graph and the
// doorways from the cell dat). Pump thread.
// v2 (2026-10-30, RynthNav routes through dungeons): drops down a ledge or shaft as a one-way step
// (the walk is split there: up to its edge, then on from where you land), and cells to keep out of
// (a locked door's doorway).
public sealed partial class RynthAiPlugin
{
    internal unsafe int DungeonPathForPlugin(uint landblock, double sx, double sy, double sz, double gx, double gy, double gz,
        double* points, int maxPoints, int* count,
        double maxDrop = 0, double* avoid = null, int avoidCount = 0, int* kinds = null)
    {
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return RynthAiDungeonPathApiV1.ResultNoData;
        landblock &= 0xFFFF;
        var graph = DungeonPathfinder.GetGraph(landblock, cellDat);
        if (graph.Count == 0) return RynthAiDungeonPathApiV1.ResultNoData;
        var geo = DungeonPathfinder.GetGeometry(landblock, cellDat, _raycast?.GeometryLoader?.DungeonLOS);

        var avoidList = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < avoidCount; i++) avoidList.Add((avoid[i * 3], avoid[i * 3 + 1], avoid[i * 3 + 2]));

        var walk = DungeonPathfinder.PluginWalk(graph, geo, sx, sy, sz, gx, gy, gz, maxDrop, avoidList, out int result);
        if (walk == null) return result;

        int n = 0;
        foreach (var p in walk)
        {
            if (n >= maxPoints) break;
            points[n * 3] = p.X; points[n * 3 + 1] = p.Y; points[n * 3 + 2] = p.Z;
            if (kinds != null) kinds[n] = p.Kind;
            n++;
        }
        if (n == maxPoints && walk.Count > maxPoints)
        {
            var last = walk[^1];
            points[(n - 1) * 3] = last.X; points[(n - 1) * 3 + 1] = last.Y; points[(n - 1) * 3 + 2] = last.Z;
            if (kinds != null) kinds[n - 1] = RynthAiDungeonPathApiV1.KindWalk;
        }
        *count = n;
        return n >= 2 ? RynthAiDungeonPathApiV1.ResultOk : RynthAiDungeonPathApiV1.ResultNoPath;
    }
}

internal static partial class DungeonPathfinder
{
    /// <summary>How far past a drop's edge the landing point is put (world units), so the walk carries on off it.</summary>
    internal const double DropRunOffMeters = 1.5;

    private static double NsOf(double y) => (y / 24.0 - 1019.5) / 10.0;
    private static double EwOf(double x) => (x / 24.0 - 1019.5) / 10.0;
    private static double YOf(double ns) => (ns * 10.0 + 1019.5) * 24.0;
    private static double XOf(double ew) => (ew * 10.0 + 1019.5) * 24.0;

    /// <summary>
    /// A walk for another plugin (RynthNav), world units, start first and goal last, with each
    /// point's kind (RynthAiDungeonPathApiV1.KindWalk, or KindDrop: the edge of a drop, the next
    /// point is where you land). Null with <paramref name="result"/> NoData / NoPath when there is none.
    /// </summary>
    internal static List<(double X, double Y, double Z, int Kind)>? PluginWalk(
        Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo,
        double sx, double sy, double sz, double gx, double gy, double gz,
        double maxDrop, IReadOnlyList<(double X, double Y, double Z)>? avoid, out int result)
    {
        result = RynthAiDungeonPathApiV1.ResultNoData;
        uint startCell = NearestCell(graph, NsOf(sy), EwOf(sx), (float)sz);
        uint goalCell = NearestCell(graph, NsOf(gy), EwOf(gx), (float)gz);
        if (startCell == 0 || goalCell == 0) return null;

        HashSet<uint>? keepOut = null;
        if (avoid != null)
            foreach (var a in avoid)
            {
                uint c = NearestCell(graph, NsOf(a.Y), EwOf(a.X), (float)a.Z);
                if (c == 0 || c == startCell || c == goalCell) continue;
                (keepOut ??= new HashSet<uint>()).Add(c);
            }

        List<uint> path = FindPath(startCell, goalCell, graph, keepOut, maxDrop);
        if (path.Count == 0) { result = RynthAiDungeonPathApiV1.ResultNoPath; return null; }

        var walk = new List<(double X, double Y, double Z, int Kind)>();
        void Put(double x, double y, double z, int kind)
        {
            if (walk.Count > 0)
            {
                var l = walk[^1];
                if (Math.Abs(l.X - x) < 0.05 && Math.Abs(l.Y - y) < 0.05 && Math.Abs(l.Z - z) < 0.5)
                {
                    if (kind != RynthAiDungeonPathApiV1.KindWalk) walk[^1] = (l.X, l.Y, l.Z, kind);
                    return;
                }
            }
            walk.Add((x, y, z, kind));
        }

        // Runs without drops, joined at each drop (walk off its edge, land, go on).
        double px = sx, py = sy, pz = sz;
        Put(sx, sy, sz, RynthAiDungeonPathApiV1.KindWalk);
        int runStart = 0;
        for (int i = 0; i < path.Count; i++)
        {
            bool last = i == path.Count - 1;
            bool dropNext = !last && graph.TryGetValue(path[i], out var na) && graph.TryGetValue(path[i + 1], out var nb) && IsDropEdge(na, nb);
            if (!last && !dropNext) continue;

            var run = path.GetRange(runStart, i - runStart + 1);
            double tx, ty, tz, lx = 0, ly = 0, lz = 0;
            if (last) { tx = gx; ty = gy; tz = gz; }
            else
            {
                DungeonNavNode a = graph[path[i]], b = graph[path[i + 1]];
                DropEdgePoints(geo, a, b, out tx, out ty, out tz, out lx, out ly, out lz);
            }

            NavRouteParser route = BuildNavRoute(run, graph, NsOf(ty), EwOf(tx), geo, keepOut, NsOf(py), EwOf(px), pz, tz);
            foreach (NavPoint p in route.Points)
            {
                if (p.Type != NavPointType.Point && p.Type != NavPointType.Checkpoint) continue;
                Put(XOf(p.EW), YOf(p.NS), p.Z * 240.0, RynthAiDungeonPathApiV1.KindWalk);
            }
            if (last) { Put(gx, gy, gz, RynthAiDungeonPathApiV1.KindWalk); break; }

            Put(tx, ty, tz, RynthAiDungeonPathApiV1.KindDrop);
            Put(lx, ly, lz, RynthAiDungeonPathApiV1.KindWalk);
            px = lx; py = ly; pz = lz;
            runStart = i + 1;
        }

        if (walk.Count < 2) { result = RynthAiDungeonPathApiV1.ResultNoPath; return null; }
        result = RynthAiDungeonPathApiV1.ResultOk;
        return walk;
    }

    /// <summary>
    /// Where a drop from <paramref name="a"/> down to <paramref name="b"/> is walked off (the opening's
    /// centre when the map geometry has it, else halfway between the cells, at a's height) and where
    /// you land (a little past it towards b, at b's height).
    /// </summary>
    internal static void DropEdgePoints(DungeonGeometry? geo, DungeonNavNode a, DungeonNavNode b,
        out double ex, out double ey, out double ez, out double lx, out double ly, out double lz)
    {
        double ax = XOf(a.EW), ay = YOf(a.NS), bx = XOf(b.EW), by = YOf(b.NS);
        if (geo != null && geo.TryGetDoorway(a.CellId, b.CellId, out DungeonDoorway d)) { ex = d.X; ey = d.Y; }
        else { ex = (ax + bx) / 2; ey = (ay + by) / 2; }
        ez = a.Z;
        double hx = bx - ax, hy = by - ay, hl = Math.Sqrt(hx * hx + hy * hy);
        if (hl > 1e-3) { lx = ex + hx / hl * DropRunOffMeters; ly = ey + hy / hl * DropRunOffMeters; }
        else { lx = ex; ly = ey; }
        lz = b.Z;
    }
}

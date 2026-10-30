using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The "RynthAi.DungeonPath" interface's work (DungeonPathInterface.cs, 2026-10-29): a walk inside a
// dungeon between two points, as the Dungeon Patrol and /ra dunnav find it (the cell graph and the
// doorways from the cell dat). Pump thread.
public sealed partial class RynthAiPlugin
{
    internal unsafe int DungeonPathForPlugin(uint landblock, double sx, double sy, double sz, double gx, double gy, double gz,
        double* points, int maxPoints, int* count)
    {
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return RynthAiDungeonPathApiV1.ResultNoData;
        landblock &= 0xFFFF;
        var graph = DungeonPathfinder.GetGraph(landblock, cellDat);
        if (graph.Count == 0) return RynthAiDungeonPathApiV1.ResultNoData;

        static double Ns(double y) => (y / 24.0 - 1019.5) / 10.0;
        static double Ew(double x) => (x / 24.0 - 1019.5) / 10.0;
        static double Y(double ns) => (ns * 10.0 + 1019.5) * 24.0;
        static double X(double ew) => (ew * 10.0 + 1019.5) * 24.0;

        double sNs = Ns(sy), sEw = Ew(sx), gNs = Ns(gy), gEw = Ew(gx);
        uint startCell = DungeonPathfinder.NearestCell(graph, sNs, sEw, (float)sz);
        uint goalCell = DungeonPathfinder.NearestCell(graph, gNs, gEw, (float)gz);
        if (startCell == 0 || goalCell == 0) return RynthAiDungeonPathApiV1.ResultNoData;
        var path = DungeonPathfinder.FindPath(startCell, goalCell, graph);
        if (path.Count == 0) return RynthAiDungeonPathApiV1.ResultNoPath;

        var geo = DungeonPathfinder.GetGeometry(landblock, cellDat, _raycast?.GeometryLoader?.DungeonLOS);
        NavRouteParser route = DungeonPathfinder.BuildNavRoute(path, graph, gNs, gEw, geo, null, sNs, sEw, sz, gz);

        int n = 0;
        void Put(double x, double y, double z)
        {
            if (n >= maxPoints) return;
            if (n > 0)
            {
                double px = points[(n - 1) * 3], py = points[(n - 1) * 3 + 1];
                if (Math.Abs(px - x) < 0.05 && Math.Abs(py - y) < 0.05) return;
            }
            points[n * 3] = x; points[n * 3 + 1] = y; points[n * 3 + 2] = z;
            n++;
        }
        Put(sx, sy, sz);
        foreach (NavPoint p in route.Points)
        {
            if (p.Type != NavPointType.Point && p.Type != NavPointType.Checkpoint) continue;
            Put(X(p.EW), Y(p.NS), p.Z * 240.0);
        }
        if (n < maxPoints) Put(gx, gy, gz);
        else { points[(maxPoints - 1) * 3] = gx; points[(maxPoints - 1) * 3 + 1] = gy; points[(maxPoints - 1) * 3 + 2] = gz; }
        *count = n;
        return n >= 2 ? RynthAiDungeonPathApiV1.ResultOk : RynthAiDungeonPathApiV1.ResultNoPath;
    }
}

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;
using Vector3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// "RynthAi.DungeonPath" v3 ObstacleAhead (2026-11-07): how high the thing in front of RynthNav's walker is,
// from the static geometry the client's dats place there (Raycasting/GeometryLoader, what line of sight uses).
// Silentkelpie: "the jump on routes should take your buffed jump into account as well as your burden ... a
// jump to get over things". Horizontal rays from the walker's feet forward, at rising heights: the lowest one
// that passes is the top of what is in the way. Pump thread (RynthNav's tick).
public sealed partial class RynthAiPlugin
{
    /// <summary>The first ray's height above the feet (lower is the ground's own bumps).</summary>
    internal const double ObstacleFirstRay = 0.3;
    /// <summary>Rays this far apart going up.</summary>
    internal const double ObstacleRayStep = 0.2;
    /// <summary>The rays start this far behind the walker.</summary>
    internal const double ObstacleBehind = 1.0;

    internal int ObstacleAheadForPlugin(uint landcell, double x, double y, double z, double headingDeg, double ahead, double maxHeight, out double topAbove)
    {
        topAbove = 0;
        List<BoundingVolume>? geometry = _raycast?.GeometryAt(landcell);
        if (geometry == null) return RynthAiDungeonPathApiV1.ResultNoData;
        return MeasureObstacle(geometry, x, y, z, headingDeg, ahead, maxHeight, out topAbove);
    }

    /// <summary>
    /// The lowest height above (x, y, z) at which a ray <paramref name="ahead"/> long, heading
    /// <paramref name="headingDeg"/>, passes every volume: ResultOk with it (or maxHeight + 1 when none up to
    /// <paramref name="maxHeight"/> does), ResultNoPath when the first ray already passes. Host-free.
    /// </summary>
    internal static int MeasureObstacle(List<BoundingVolume> geometry, double x, double y, double z, double headingDeg, double ahead, double maxHeight, out double topAbove)
    {
        topAbove = 0;
        if (geometry.Count == 0) return RynthAiDungeonPathApiV1.ResultNoPath;
        ahead = Math.Clamp(ahead, 0.5, 20.0);
        maxHeight = Math.Clamp(maxHeight, ObstacleFirstRay, 12.0);
        double rad = headingDeg * Math.PI / 180.0;
        double ux = Math.Sin(rad), uy = Math.Cos(rad);
        // From a metre behind the feet: the ray test leaves out hits within 0.5 m of its start, and a walker
        // stuck against a wall stands about that close to it.
        float sx = (float)(x - ux * ObstacleBehind), sy = (float)(y - uy * ObstacleBehind);
        float ex = (float)(x + ux * ahead), ey = (float)(y + uy * ahead);
        for (double h = ObstacleFirstRay; h <= maxHeight + 1e-6; h += ObstacleRayStep)
        {
            var from = new Vector3(sx, sy, (float)(z + h));
            var to = new Vector3(ex, ey, (float)(z + h));
            if (!RaycastEngine.IsLinearPathBlocked(from, to, geometry))
            {
                if (h <= ObstacleFirstRay + 1e-6) return RynthAiDungeonPathApiV1.ResultNoPath;
                topAbove = h;
                return RynthAiDungeonPathApiV1.ResultOk;
            }
        }
        topAbove = maxHeight + 1;
        return RynthAiDungeonPathApiV1.ResultOk;
    }
}

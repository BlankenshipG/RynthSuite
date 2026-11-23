// ============================================================================
//  NavSteepGround.cs — does a straight walk climb ground too steep to walk?
//
//  The same rule that paints RynthVision's red slope overlay and keeps the
//  RynthNav navmesh off those slopes: a landscape triangle whose normal Z is
//  below ACE PhysicsGlobals.FloorZ (TerrainSampler.FloorZ, about 48°) can't be
//  walked up. Each 24 m cell is split along the diagonal AC picks per cell
//  (TerrainSampler.SwToNeCut), so the triangle under a point is the real one.
//  Going down such a slope works in AC, so a leg that runs clearly downhill
//  over it is not blocked; climbing it, or crossing it sideways, is.
//
//  NavigationEngine asks this (through the recovery planner) before walking a
//  route leg, so a straight run that would end against a red block is replaced
//  by a RynthNav navmesh way round before the character gets stuck there.
//  Pure functions over the landblocks' vertex heights: the tests drive them.
// ============================================================================

using System;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthAi;

internal static class NavSteepGround
{
    /// <summary>Distance between samples along a leg, in yards (world units).</summary>
    internal const double StepYd = 1.0;
    private const double CellYd = 24.0;
    private const double BlockYd = 192.0;
    // A steep triangle is only passable going down it: the leg must head within 60° of
    // straight downhill (its fall per yard at least half the triangle's steepest fall).
    private const double DownhillShare = 0.5;

    /// <summary>
    /// Yards along the straight line (ax, ay) → (bx, by) (world units, east / north) to the
    /// first spot where it climbs or crosses ground too steep to walk; -1 when it never does.
    /// The first <paramref name="skipStartYd"/> and last <paramref name="skipEndYd"/> are not
    /// checked (where the character and the waypoint stand), nor anything past
    /// <paramref name="maxYd"/>. <paramref name="heights"/> gives a landblock's 9×9 vertex
    /// heights (HeightsZ[ix, iy]) by landblock key (x &lt;&lt; 8 | y), or null when it isn't known.
    /// </summary>
    internal static double FirstBlockedYd(double ax, double ay, double bx, double by,
        Func<uint, float[,]?> heights, double skipStartYd, double skipEndYd, double maxYd)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return -1;
        double ux = dx / len, uy = dy / len;
        double end = Math.Min(len - skipEndYd, maxYd);

        uint lastKey = uint.MaxValue;
        float[,]? grid = null;
        for (double s = Math.Max(0, skipStartYd); s <= end; s += StepYd)
        {
            double x = ax + ux * s, y = ay + uy * s;
            if (x < 0 || y < 0 || x >= 256 * BlockYd || y >= 256 * BlockYd) continue;
            int lbX = (int)(x / BlockYd), lbY = (int)(y / BlockYd);
            uint key = (uint)((lbX << 8) | lbY);
            if (key != lastKey) { grid = heights(key); lastKey = key; }
            if (grid == null || grid.GetLength(0) < 9 || grid.GetLength(1) < 9) continue;
            if (BlocksAt(grid, key, x - lbX * BlockYd, y - lbY * BlockYd, ux, uy)) return s;
        }
        return -1;
    }

    /// <summary>
    /// True when walking in direction (ux, uy) at landblock-local (lx, ly) is blocked: the
    /// triangle there is too steep to walk and the direction doesn't run clearly downhill.
    /// </summary>
    internal static bool BlocksAt(float[,] h, uint landblockKey, double lx, double ly, double ux, double uy)
    {
        int cx = Math.Clamp((int)(lx / CellYd), 0, 7);
        int cy = Math.Clamp((int)(ly / CellYd), 0, 7);
        double fx = lx - cx * CellYd, fy = ly - cy * CellYd;
        double h00 = h[cx, cy], h10 = h[cx + 1, cy], h01 = h[cx, cy + 1], h11 = h[cx + 1, cy + 1];

        // The plane's rise per yard east (gx) and north (gy) on the triangle under the point,
        // with the triangles TerrainSampler.GetTrianglePassability names for each split.
        double gx, gy;
        if (TerrainSampler.SwToNeCut(landblockKey, cx, cy))
        {
            if (fy <= fx) { gx = (h10 - h00) / CellYd; gy = (h11 - h10) / CellYd; }   // SE half: SW, SE, NE
            else          { gx = (h11 - h01) / CellYd; gy = (h01 - h00) / CellYd; }   // NW half: SW, NE, NW
        }
        else
        {
            if (fx + fy <= CellYd) { gx = (h10 - h00) / CellYd; gy = (h01 - h00) / CellYd; }   // SW half: SW, SE, NW
            else                   { gx = (h11 - h01) / CellYd; gy = (h11 - h10) / CellYd; }   // NE half: SE, NE, NW
        }

        double slope = Math.Sqrt(gx * gx + gy * gy);
        double normalZ = 1.0 / Math.Sqrt(1.0 + slope * slope);
        if (normalZ >= TerrainSampler.FloorZ) return false;
        double rise = gx * ux + gy * uy;            // yards up per yard walked
        return rise > -DownhillShare * slope;
    }
}

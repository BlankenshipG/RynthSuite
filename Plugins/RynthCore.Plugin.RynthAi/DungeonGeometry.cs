// DungeonGeometry: the openings, floors and walls of a dungeon landblock, for laying routes.
//
// DungeonPathfinder walks the EnvCell portal graph. A graph edge says two cells share an
// opening; this class says where that opening is, how wide it is and which way it faces,
// from the portal polygons of the dungeon map geometry DungeonLOS already builds (render
// polygons with the portal flag, one in each of the two cells, at the same spot). With that:
//
//   - a NARROW opening (a doorway, a hallway mouth, the joint between two corridor pieces)
//     gets an approach point centred in front of it and an exit point centred beyond it, on
//     its centre line, so the character lines up and goes through straight;
//   - a WIDE opening (8 m or more: a whole 10 m cell side, or the diagonal of one) is no
//     opening at all to a player: it joins two cells of one big room. DungeonPathfinder
//     merges those cells into one room and crosses it directly.
//
// Floors (physics floor polygons) and walls (upright render polygons) say whether a straight
// line across a room stays on its floor and clear of its walls (an L-shaped room, a pillar).
//
// World units throughout (metres; the nav code calls them yards): x east, y north, z up.
// Pure data and maths; Build takes plain polygon lists, so tests feed it fixtures.

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One opening between two cells.</summary>
internal sealed class DungeonDoorway
{
    public uint   CellA, CellB;
    /// <summary>Centre of the opening at floor level (world units; z = its lowest vertex).</summary>
    public double X, Y, Z;
    /// <summary>Horizontal unit normal, pointing from CellA into CellB.</summary>
    public double Nx, Ny;
    /// <summary>Horizontal width of the opening.</summary>
    public double Width;

    public bool IsWide => Width >= DungeonGeometry.WideOpeningMeters;

    /// <summary>The normal pointing out of <paramref name="fromCell"/> through the opening.</summary>
    public (double Nx, double Ny) NormalFrom(uint fromCell) => fromCell == CellA ? (Nx, Ny) : (-Nx, -Ny);
}

internal sealed class DungeonGeometry
{
    /// <summary>An opening at least this wide joins two cells of one room (a 10 m cell side is
    /// open). Corridor and doorway openings in AC dungeons are about 5 m; 0x6346 has 5.3 m
    /// (568 pairs), 7 m (2), 10 m (60) and 14 m (35).</summary>
    public const double WideOpeningMeters = 8.0;

    /// <summary>Portal polygons in two cells are the same opening when their centres are this close.</summary>
    private const double PairTolerance = 1.5;

    // A floor counts under a point when it is at most this far above / below the given height.
    private const double FloorAbove = 1.0, FloorBelow = 4.0;

    // Walls that matter for walking: they reach into this band above the floor. Lower lips
    // and kerbs are what snags at doorways, so the band starts low.
    private const double WallBandLow = 0.25, WallBandHigh = 1.8;

    private readonly Dictionary<ulong, DungeonDoorway> _doorways = new();
    private readonly Dictionary<uint, List<Vector3[]>> _floors = new();
    private readonly Dictionary<uint, List<WallSeg>> _walls = new();

    private readonly struct WallSeg
    {
        public readonly double X0, Y0, X1, Y1, ZMin, ZMax;
        public WallSeg(double x0, double y0, double x1, double y1, double zMin, double zMax)
        { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; ZMin = zMin; ZMax = zMax; }
    }

    public int DoorwayCount => _doorways.Count;
    public int FloorCellCount => _floors.Count;

    // ── Building ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the geometry for a cell graph. <paramref name="portalPolys"/> are the polygons
    /// flagged as portals (render geometry), <paramref name="floorPolys"/> the upward-facing
    /// physics floor polygons, <paramref name="wallPolys"/> the other render polygons (only
    /// the upright ones are used). Any list may be empty: missing data just means fewer
    /// doorways (the pathfinder falls back to cell-centre midpoints) or no floor/wall checks.
    /// </summary>
    public static DungeonGeometry Build(
        IReadOnlyDictionary<uint, DungeonNavNode> graph,
        IEnumerable<(uint CellId, Vector3[] Verts)> portalPolys,
        IEnumerable<(uint CellId, Vector3[] Verts)> floorPolys,
        IEnumerable<(uint CellId, Vector3[] Verts)> wallPolys)
    {
        var g = new DungeonGeometry();

        foreach (var (cell, v) in floorPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!IsUpwardFloor(v)) continue;   // the "floor" list holds ceilings too
            if (!g._floors.TryGetValue(cell, out var list)) g._floors[cell] = list = new List<Vector3[]>();
            list.Add(v);
        }

        foreach (var (cell, v) in wallPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!TryWallFootprint(v, out var seg)) continue;
            if (!g._walls.TryGetValue(cell, out var list)) g._walls[cell] = list = new List<WallSeg>();
            list.Add(seg);
        }

        // Portal polygons per cell: centre, horizontal normal, width.
        var portals = new Dictionary<uint, List<PortalPoly>>();
        foreach (var (cell, v) in portalPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!TryPortalPoly(v, out var pp)) continue;
            if (!portals.TryGetValue(cell, out var list)) portals[cell] = list = new List<PortalPoly>();
            list.Add(pp);
        }

        foreach (var node in graph.Values)
        {
            foreach (uint nb in node.Neighbors)
            {
                if (nb <= node.CellId) continue;                 // each edge once
                if (!graph.TryGetValue(nb, out var other)) continue;
                if (!portals.TryGetValue(node.CellId, out var aList) || !portals.TryGetValue(nb, out var bList)) continue;
                if (TryMatch(g, node, other, aList, bList, out var door))
                    g._doorways[EdgeKey(node.CellId, nb)] = door;
            }
        }
        return g;
    }

    private readonly struct PortalPoly
    {
        public readonly double X, Y, Z, CentreZ, Nx, Ny, Width;
        public PortalPoly(double x, double y, double z, double cz, double nx, double ny, double w)
        { X = x; Y = y; Z = z; CentreZ = cz; Nx = nx; Ny = ny; Width = w; }
    }

    private static bool TryPortalPoly(Vector3[] v, out PortalPoly pp)
    {
        pp = default;
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-6) return false;
        double hx = nx / len, hy = ny / len;
        double h = Math.Sqrt(hx * hx + hy * hy);
        // A floor or ceiling portal (a shaft) has no horizontal facing: not a doorway.
        if (h < 0.5) return false;
        hx /= h; hy /= h;

        // Width along the opening (perpendicular to the normal, horizontal); centre at the
        // middle of that span, the mean along the normal, at the lowest vertex.
        double px = -hy, py = hx;
        double minS = double.MaxValue, maxS = double.MinValue, sumN = 0, minZ = double.MaxValue, sumZ = 0;
        foreach (var p in v)
        {
            double s = p.X * px + p.Y * py;
            if (s < minS) minS = s;
            if (s > maxS) maxS = s;
            sumN += p.X * hx + p.Y * hy;
            if (p.Z < minZ) minZ = p.Z;
            sumZ += p.Z;
        }
        double midS = (minS + maxS) * 0.5, meanN = sumN / v.Length;
        pp = new PortalPoly(px * midS + hx * meanN, py * midS + hy * meanN, minZ, sumZ / v.Length, hx, hy, maxS - minS);
        return true;
    }

    private static bool TryMatch(DungeonGeometry g, DungeonNavNode a, DungeonNavNode b,
                                 List<PortalPoly> aList, List<PortalPoly> bList, out DungeonDoorway door)
    {
        door = null!;
        double ax = NavToWorld(a.EW), ay = NavToWorld(a.NS), bx = NavToWorld(b.EW), by = NavToWorld(b.NS);
        double midX = (ax + bx) * 0.5, midY = (ay + by) * 0.5;
        double best = double.MaxValue;
        PortalPoly hit = default;
        bool found = false;
        foreach (var pa in aList)
        {
            foreach (var pb in bList)
            {
                double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.CentreZ - pb.CentreZ;
                if (dx * dx + dy * dy + dz * dz > PairTolerance * PairTolerance) continue;
                // If two cells share more than one opening, take the one nearest the line between them.
                double dm = (pa.X - midX) * (pa.X - midX) + (pa.Y - midY) * (pa.Y - midY);
                if (dm < best) { best = dm; hit = pa; found = true; }
            }
        }
        if (!found) return false;

        double nx = hit.Nx, ny = hit.Ny;
        // Orient A -> B. The floors decide when they can (a cell's origin isn't always its
        // middle): a step back from the opening must be on A's floor, a step forward on B's.
        int vote = 0;
        if (g.HasFloor(a.CellId)) vote += g.OnCellFloor(a.CellId, hit.X - nx, hit.Y - ny, hit.Z) ? 1 : g.OnCellFloor(a.CellId, hit.X + nx, hit.Y + ny, hit.Z) ? -1 : 0;
        if (g.HasFloor(b.CellId)) vote += g.OnCellFloor(b.CellId, hit.X + nx, hit.Y + ny, hit.Z) ? 1 : g.OnCellFloor(b.CellId, hit.X - nx, hit.Y - ny, hit.Z) ? -1 : 0;
        if (vote == 0) vote = (bx - ax) * nx + (by - ay) * ny >= 0 ? 1 : -1;   // cell centres
        if (vote < 0) { nx = -nx; ny = -ny; }

        door = new DungeonDoorway
        {
            CellA = a.CellId, CellB = b.CellId,
            X = hit.X, Y = hit.Y, Z = hit.Z,
            Nx = nx, Ny = ny,
            Width = hit.Width,
        };
        return true;
    }

    private static bool TryWallFootprint(Vector3[] v, out WallSeg seg)
    {
        seg = default;
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-6) return false;
        if (Math.Abs(nz) / len > 0.5) return false;         // floor / ceiling / ramp, not a wall
        double hx = nx, hy = ny, h = Math.Sqrt(hx * hx + hy * hy);
        if (h < 1e-9) return false;
        double px = -hy / h, py = hx / h;                     // along the wall
        double minS = double.MaxValue, maxS = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        double sx0 = 0, sy0 = 0, sx1 = 0, sy1 = 0;
        foreach (var p in v)
        {
            double s = p.X * px + p.Y * py;
            if (s < minS) { minS = s; sx0 = p.X; sy0 = p.Y; }
            if (s > maxS) { maxS = s; sx1 = p.X; sy1 = p.Y; }
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        if (maxS - minS < 0.05) return false;
        seg = new WallSeg(sx0, sy0, sx1, sy1, minZ, maxZ);
        return true;
    }

    /// <summary>
    /// A polygon facing up (counter-clockwise seen from above). DungeonLOS's floor polygon list
    /// keeps every near-horizontal physics polygon, ceilings included: at 0x6346 the corridor
    /// ceilings 3.8 m up are in it, and some cells have only their ceiling there.
    /// </summary>
    internal static bool IsUpwardFloor(Vector3[] v)
    {
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 1e-6 && nz / len >= 0.4;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    public bool TryGetDoorway(uint a, uint b, out DungeonDoorway door) => _doorways.TryGetValue(EdgeKey(a, b), out door!);

    public bool HasFloor(uint cell) => _floors.ContainsKey(cell);

    /// <summary>True when (x, y) is on one of the cell's floor polygons, the floor at most 1 m
    /// above and 4 m below z. False when the cell has no floor data.</summary>
    public bool OnCellFloor(uint cell, double x, double y, double z)
    {
        if (!_floors.TryGetValue(cell, out var list)) return false;
        foreach (var v in list)
        {
            if (!InsidePolygon2D(v, x, y)) continue;
            double fz = FloorZAt(v);
            double dz = z - fz;
            if (dz >= -FloorAbove && dz <= FloorBelow) return true;
        }
        return false;
    }

    /// <summary>True when (x, y) is on the floor of any of <paramref name="cells"/>; also true
    /// when none of them has floor data (nothing to check against).</summary>
    public bool OnFloorOfAny(IEnumerable<uint> cells, double x, double y, double z)
    {
        bool anyFloor = false;
        foreach (uint c in cells)
        {
            if (!_floors.ContainsKey(c)) continue;
            anyFloor = true;
            if (OnCellFloor(c, x, y, z)) return true;
        }
        return !anyFloor;
    }

    /// <summary>
    /// Can a character walk the straight line (x0, y0) to (x1, y1) at floor height z, inside
    /// <paramref name="cells"/> (one room)? The line and two lines <paramref name="clearance"/>
    /// to either side must stay on the room's floor (sampled every half metre) and cross none of
    /// its walls that reach into knee-to-head height.
    /// </summary>
    public bool SegmentClear(IReadOnlyCollection<uint> cells, double x0, double y0, double z0,
                             double x1, double y1, double z1, double clearance = 0.5)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return true;
        double ox = -dy / len * clearance, oy = dx / len * clearance;

        // Floor: centre line and both sides.
        bool checkFloor = false;
        foreach (uint c in cells) if (_floors.ContainsKey(c)) { checkFloor = true; break; }
        if (checkFloor)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double x = x0 + dx * t, y = y0 + dy * t, z = z0 + (z1 - z0) * t;
                if (!OnFloorOfAny(cells, x, y, z)) return false;
                if (!OnFloorOfAny(cells, x + ox, y + oy, z)) return false;
                if (!OnFloorOfAny(cells, x - ox, y - oy, z)) return false;
            }
        }

        // Walls.
        double zLo = Math.Min(z0, z1) + WallBandLow, zHi = Math.Max(z0, z1) + WallBandHigh;
        foreach (uint c in cells)
        {
            if (!_walls.TryGetValue(c, out var list)) continue;
            foreach (var w in list)
            {
                if (w.ZMax < zLo || w.ZMin > zHi) continue;
                if (SegmentsCross(x0, y0, x1, y1, w.X0, w.Y0, w.X1, w.Y1)) return false;
                if (SegmentsCross(x0 + ox, y0 + oy, x1 + ox, y1 + oy, w.X0, w.Y0, w.X1, w.Y1)) return false;
                if (SegmentsCross(x0 - ox, y0 - oy, x1 - ox, y1 - oy, w.X0, w.Y0, w.X1, w.Y1)) return false;
            }
        }
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static ulong EdgeKey(uint a, uint b)
        => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    internal static double NavToWorld(double navCoord) => (navCoord * 10.0 + 1019.5) * 24.0;
    internal static double WorldToNav(double world) => (world / 24.0 - 1019.5) / 10.0;

    private static void NewellNormal(Vector3[] v, out double nx, out double ny, out double nz)
    {
        nx = 0; ny = 0; nz = 0;
        int n = v.Length;
        for (int i = 0; i < n; i++)
        {
            var a = v[i]; var b = v[(i + 1) % n];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
    }

    private static double FloorZAt(Vector3[] v)
    {
        double z = 0;
        foreach (var p in v) z += p.Z;
        return z / v.Length;
    }

    internal static bool InsidePolygon2D(Vector3[] v, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            double xi = v[i].X, yi = v[i].Y, xj = v[j].X, yj = v[j].Y;
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    // Proper crossing of two 2D segments (touching at an end point does not count).
    private static bool SegmentsCross(double ax, double ay, double bx, double by,
                                      double cx, double cy, double dx, double dy)
    {
        double d1 = Cross(cx, cy, dx, dy, ax, ay);
        double d2 = Cross(cx, cy, dx, dy, bx, by);
        double d3 = Cross(ax, ay, bx, by, cx, cy);
        double d4 = Cross(ax, ay, bx, by, dx, dy);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
               ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}

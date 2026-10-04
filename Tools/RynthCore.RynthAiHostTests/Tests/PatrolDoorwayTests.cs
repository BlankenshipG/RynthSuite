using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// Dungeon routes laid on real openings (DungeonGeometry, DungeonPathfinder.Rooms.cs), on small
// hand-built dungeons: cells as floor rectangles, openings as portal polygons in both cells,
// an optional wall. World units (metres), x east, y north, floors at z = 0.
//   - a doorway gets an approach point centred in front of it and an exit point centred
//     beyond it, flagged NavPoint.Doorway;
//   - openings close together in a row (thin joint cells) are one run: nothing doubles back;
//   - a 3x3 room joined by wide openings is one room, crossed straight from door to door
//     (the old cell walk zig-zagged through it); an L-shaped room or a pillar bends the
//     crossing round the corner;
//   - the patrol over such a dungeon puts no point inside the big room and still goes
//     through every doorway; a big dead-end room is looked into;
//   - ceilings are not floors.
internal static class PatrolDoorwayTests
{
    public static void Register(Runner r)
    {
        r.Add("dungeon doorways: an opening's centre, facing and width come from its portal polygons", DoorwayFromPortals);
        r.Add("dungeon doorways: approach and exit points centred on the opening, on its centre line, flagged", ApproachAndExit);
        r.Add("dungeon doorways: openings 0.7 m apart (joint cells) are one run, nothing doubles back", JointRun);
        r.Add("dungeon rooms: cells joined by wide openings, or with one origin, are one room; hazards never merge", RoomsMerge);
        r.Add("dungeon rooms: a big room is crossed straight from door to door (old walk: 12 points)", BigRoomCrossedDirectly);
        r.Add("dungeon rooms: an L-shaped room's crossing goes round the corner, on the floor", LShapedRoom);
        r.Add("dungeon rooms: a pillar in the way bends the crossing round it", PillarInTheWay);
        r.Add("dungeon patrol: a loop through a big room: no points inside it, every doorway taken", PatrolLoop);
        r.Add("dungeon patrol: a big dead-end room is looked into, not skirted", PatrolLooksIntoDeadEndRoom);
        r.Add("dungeon floors: a ceiling (facing down) is not a floor", CeilingIsNotFloor);
    }

    // ── Fixture builder ──────────────────────────────────────────────────────

    private sealed class Dungeon
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new();
        public readonly List<(uint, V3[])> Floors = new();
        public readonly List<(uint, V3[])> Walls = new();
        private uint _next = 0x63460100;

        /// <summary>A cell whose floor is the rectangle (x0,y0)-(x1,y1); its origin is the middle.</summary>
        public uint Cell(double x0, double y0, double x1, double y1)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode
            {
                CellId = id,
                EW = DungeonGeometry.WorldToNav((x0 + x1) / 2),
                NS = DungeonGeometry.WorldToNav((y0 + y1) / 2),
                Z = 0,
            };
            Floors.Add((id, Rect(x0, y0, x1, y1, 0)));
            return id;
        }

        /// <summary>An opening between a and b on the vertical line x (from y0 to y1).</summary>
        public void OpenX(uint a, uint b, double x, double y0, double y1) => Open(a, b, VerticalQuad(x, y0, x, y1));

        /// <summary>An opening between a and b on the horizontal line y (from x0 to x1).</summary>
        public void OpenY(uint a, uint b, double y, double x0, double x1) => Open(a, b, VerticalQuad(x0, y, x1, y));

        private void Open(uint a, uint b, V3[] quad)
        {
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));   // the other cell's copy, wound the other way
        }

        public void Wall(uint cell, double x0, double y0, double x1, double y1) => Walls.Add((cell, VerticalQuad(x0, y0, x1, y1)));

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);
    }

    private static V3[] Rect(double x0, double y0, double x1, double y1, double z) => new[]
    {
        new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y0, (float)z),
        new V3((float)x1, (float)y1, (float)z), new V3((float)x0, (float)y1, (float)z),
    };

    private static V3[] VerticalQuad(double x0, double y0, double x1, double y1) => new[]
    {
        new V3((float)x0, (float)y0, 0f), new V3((float)x1, (float)y1, 0f),
        new V3((float)x1, (float)y1, 3f), new V3((float)x0, (float)y0, 3f),
    };

    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);

    private static List<NavPoint> Path(Dungeon d, DungeonGeometry? geo, double sx, double sy, double dx, double dy, params uint[] cells)
        => DungeonPathfinder.BuildPathPoints(cells.ToList(), d.Graph, geo, null,
            DungeonGeometry.WorldToNav(sy), DungeonGeometry.WorldToNav(sx), 0,
            DungeonGeometry.WorldToNav(dy), DungeonGeometry.WorldToNav(dx), 0, out _);

    // Every leg (start, points..., dest) stays on the floor of the given cells and clear of walls.
    private static bool LegsClear(DungeonGeometry geo, IReadOnlyCollection<uint> cells, double sx, double sy, List<NavPoint> pts, double dx, double dy)
    {
        var xs = new List<(double X, double Y)> { (sx, sy) };
        xs.AddRange(pts.Select(p => (X(p), Y(p))));
        xs.Add((dx, dy));
        for (int i = 0; i + 1 < xs.Count; i++)
            if (!geo.SegmentClear(cells, xs[i].X, xs[i].Y, 0, xs[i + 1].X, xs[i + 1].Y, 0, 0.45)) return false;
        return true;
    }

    // ── A room (x 0..10, y 0..10) and a corridor east of it (x 10..20, 5.3 m wide) ────

    private static (Dungeon D, uint Room, uint Corr) RoomAndCorridor()
    {
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        uint corr = d.Cell(10, 2.35, 20, 7.65);
        d.OpenX(room, corr, 10, 2.35, 7.65);
        return (d, room, corr);
    }

    private static void DoorwayFromPortals()
    {
        var (d, room, corr) = RoomAndCorridor();
        var geo = d.Geo();
        Check.True(geo.TryGetDoorway(room, corr, out var door), "the opening is matched from the two portal polygons");
        Check.Near(door.X, 10, 1e-4, "centre x");
        Check.Near(door.Y, 5, 1e-4, "centre y (middle of the opening)");
        Check.Near(door.Z, 0, 1e-4, "at floor level (its lowest vertex), not halfway up");
        Check.Near(door.Width, 5.3, 1e-3, "width");
        Check.False(door.IsWide, "5.3 m is a doorway, not a room joint");
        var (nx, ny) = door.NormalFrom(room);
        Check.Near(nx, 1, 1e-6, "faces from the room into the corridor (east)");
        Check.Near(ny, 0, 1e-6, "faces due east");
        Check.True(geo.TryGetDoorway(corr, room, out _), "found from either side");
    }

    private static void ApproachAndExit()
    {
        var (d, room, corr) = RoomAndCorridor();
        var geo = d.Geo();
        // From the room's far corner to the corridor's end.
        var pts = Path(d, geo, 1, 9, 18, 5, room, corr);
        Check.Eq(pts.Count, 2, "an approach and an exit point, nothing else (the room is open)");
        Check.Near(X(pts[0]), 6, 1e-3, "approach 4 m in front of the opening");
        Check.Near(Y(pts[0]), 5, 1e-3, "approach centred on the opening");
        Check.Near(X(pts[1]), 14, 1e-3, "exit 4 m beyond it");
        Check.Near(Y(pts[1]), 5, 1e-3, "exit centred on the opening");
        Check.True(pts.All(p => p.Doorway), "both flagged as doorway points");

        // The old cell walk: 30% / 50% of the way between the cell centres, no flag.
        var old = Path(d, null, 1, 9, 18, 5, room, corr);
        Check.False(old.Any(p => p.Doorway), "without geometry: the old points, unflagged");

        // An opening off the line between the cell centres: the points still sit on its centre line.
        var d2 = new Dungeon();
        uint big = d2.Cell(0, 0, 10, 20);
        uint side = d2.Cell(10, 13, 20, 18);
        d2.OpenX(big, side, 10, 13, 18);
        var p2 = Path(d2, d2.Geo(), 2, 2, 18, 15.5, big, side);
        Check.Near(Y(p2[0]), 15.5, 1e-3, "approach on the opening's centre line (y 15.5), not the cell centres' line");
        Check.Near(Y(p2[1]), 15.5, 1e-3, "exit on it too");
    }

    private static void JointRun()
    {
        // Room, two 0.7 m joint cells, corridor: openings at x = 10, 10.7, 11.4.
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        uint j1 = d.Cell(10, 2.35, 10.7, 7.65);
        uint j2 = d.Cell(10.7, 2.35, 11.4, 7.65);
        uint corr = d.Cell(11.4, 2.35, 21.4, 7.65);
        d.OpenX(room, j1, 10, 2.35, 7.65);
        d.OpenX(j1, j2, 10.7, 2.35, 7.65);
        d.OpenX(j2, corr, 11.4, 2.35, 7.65);
        var pts = Path(d, d.Geo(), 2, 5, 20, 5, room, j1, j2, corr);
        var xs = pts.Select(X).ToList();
        Check.True(xs.Zip(xs.Skip(1), (a, b) => b > a).All(b => b), $"x only increases ({string.Join(", ", xs.Select(x => x.ToString("F2")))})");
        Check.Eq(xs.Count(x => x > 9.9 && x < 11.5), 0, "no point among the joints");
        Check.Near(xs.First(), 6, 1e-3, "one approach, 4 m in front of the first opening");
        Check.Near(xs.Last(), 15.4, 1e-3, "one exit, 4 m beyond the last");
        Check.True(pts.All(p => Math.Abs(Y(p) - 5) < 1e-3), "all on the centre line");
    }

    // ── A 3x3 room of 10 m cells (x 0..30, y 0..30), wide openings inside ────────

    private static (Dungeon D, uint[,] C) BigRoom()
    {
        var d = new Dungeon();
        var c = new uint[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = d.Cell(i * 10, j * 10, i * 10 + 10, j * 10 + 10);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i < 2) d.OpenX(c[i, j], c[i + 1, j], i * 10 + 10, j * 10, j * 10 + 10);
                if (j < 2) d.OpenY(c[i, j], c[i, j + 1], j * 10 + 10, i * 10, i * 10 + 10);
            }
        return (d, c);
    }

    private static void RoomsMerge()
    {
        var (d, c) = BigRoom();
        uint west = d.Cell(-10, 22.35, 0, 27.65);
        d.OpenX(west, c[0, 2], 0, 22.35, 27.65);
        var geo = d.Geo();
        var rooms = DungeonPathfinder.BuildRooms(d.Graph, geo);
        int r = rooms.RoomOf[c[1, 1]];
        Check.Eq(rooms.Cells[r].Count, 9, "the nine cells are one room");
        Check.True(rooms.RoomOf[west] != r, "the corridor behind a doorway is a room of its own");

        var hazard = new HashSet<uint> { c[1, 1] };
        var withHazard = DungeonPathfinder.BuildRooms(d.Graph, geo, hazard);
        Check.Eq(withHazard.Cells[withHazard.RoomOf[c[1, 1]]].Count, 1, "a hazard cell stays a room of its own");
        Check.Eq(withHazard.Cells[withHazard.RoomOf[c[0, 0]]].Count, 8, "the rest still join round it");

        // Pieces of one junction: two cells with one origin, joined by a narrow opening.
        var j = new Dungeon();
        uint p1 = j.Cell(0, 0, 10, 10);
        uint p2 = j.Cell(2, 2, 8, 8);            // same middle (5, 5)
        uint off = j.Cell(10, 2.35, 20, 7.65);
        j.OpenX(p1, off, 10, 2.35, 7.65);
        j.OpenY(p1, p2, 2, 2.35, 7.65);
        var jr = DungeonPathfinder.BuildRooms(j.Graph, j.Geo());
        Check.Eq(jr.RoomOf[p1], jr.RoomOf[p2], "cells with the same origin are one junction (one room)");
        Check.True(jr.RoomOf[off] != jr.RoomOf[p1], "a neighbour through a doorway is not");
    }

    private static (Dungeon D, uint[,] C, uint West, uint East) BigRoomWithDoors()
    {
        var (d, c) = BigRoom();
        uint west = d.Cell(-10, 22.35, 0, 27.65);   // doorway into the room's north-west cell
        uint east = d.Cell(30, 2.35, 40, 7.65);      // doorway out of its south-east cell
        d.OpenX(west, c[0, 2], 0, 22.35, 27.65);
        d.OpenX(c[2, 0], east, 30, 2.35, 7.65);
        return (d, c, west, east);
    }

    private static void BigRoomCrossedDirectly()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        var geo = d.Geo();
        // A* style cell path that zig-zags through the room.
        uint[] cells = { west, c[0, 2], c[1, 2], c[1, 1], c[2, 1], c[2, 0], east };
        var pts = Path(d, geo, -8, 25, 38, 5, cells);
        Check.Eq(pts.Count, 4, "approach + exit at each doorway, nothing in between");
        Check.Near(X(pts[1]), 4, 1e-3, "in through the west doorway");
        Check.Near(X(pts[2]), 26, 1e-3, "then straight to the east doorway's approach");
        Check.Near(Y(pts[2]), 5, 1e-3, "centred on it");

        var old = Path(d, null, -8, 25, 38, 5, cells);
        Check.Eq(old.Count, 12, "the old walk: two points per cell step");
    }

    private static void LShapedRoom()
    {
        var d = new Dungeon();
        uint c00 = d.Cell(0, 0, 10, 10), c01 = d.Cell(0, 10, 10, 20), c10 = d.Cell(10, 0, 20, 10);
        d.OpenY(c00, c01, 10, 0, 10);
        d.OpenX(c00, c10, 10, 0, 10);
        uint west = d.Cell(-10, 12.35, 0, 17.65), east = d.Cell(20, 2.35, 30, 7.65);
        d.OpenX(west, c01, 0, 12.35, 17.65);
        d.OpenX(c10, east, 20, 2.35, 7.65);
        var geo = d.Geo();
        var pts = Path(d, geo, -8, 15, 28, 5, west, c01, c00, c10, east);
        var room = new[] { c00, c01, c10 };
        var turns = pts.Where(p => !p.Doorway).ToList();
        Check.Eq(turns.Count, 1, "one turn point inside the room besides the doorway points");
        // The east door's approach point (16, 5) is in line with the turn point and the exit
        // beyond the door, so it is dropped as collinear; the legs are checked to it.
        Check.True(LegsClear(geo, room, 4, 15, turns, 16, 5), "both legs across the room stay on its floor");
        Check.False(geo.SegmentClear(room, 4, 15, 0, 16, 5, 0, 0.45), "the straight line would have cut the inside corner");
        Check.Near(Y(pts[^1]), 5, 1e-3, "out through the east door on its centre line");
    }

    private static void PillarInTheWay()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        // A wall across the room's middle, right on the diagonal from door to door.
        d.Wall(c[1, 1], 11, 19, 19, 11);
        var geo = d.Geo();
        var room = Enumerable.Range(0, 9).Select(k => c[k / 3, k % 3]).ToArray();
        var pts = Path(d, geo, -8, 25, 38, 5, west, c[0, 2], c[1, 2], c[1, 1], c[2, 1], c[2, 0], east);
        var turns = pts.Where(p => !p.Doorway).ToList();
        Check.True(turns.Count >= 1, "a turn point to get round the wall");
        Check.False(geo.SegmentClear(room, 4, 25, 0, 26, 5, 0, 0.45), "the straight line would hit the wall");
        Check.True(LegsClear(geo, room, 4, 25, turns, 26, 5), "every leg across the room misses the wall");
    }

    // ── Patrol ───────────────────────────────────────────────────────────────

    // The big room with a corridor loop round its north side: west door -> up, along the top,
    // down the east side -> a corner cell -> the east door's corridor.
    private static (Dungeon D, uint[,] C, uint West, uint Top2, uint East) LoopDungeon()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        uint up1 = d.Cell(-10, 27.65, -4.7, 37.65);
        d.OpenY(west, up1, 27.65, -10, -4.7);
        uint top1 = d.Cell(-10, 37.65, 10, 42.95);
        d.OpenY(up1, top1, 37.65, -10, -4.7);
        uint top2 = d.Cell(10, 37.65, 30, 42.95);
        d.OpenX(top1, top2, 10, 37.65, 42.95);
        uint top3 = d.Cell(30, 37.65, 45.3, 42.95);
        d.OpenX(top2, top3, 30, 37.65, 42.95);
        uint down1 = d.Cell(40, 7.65, 45.3, 37.65);
        d.OpenY(down1, top3, 37.65, 40, 45.3);
        uint corner = d.Cell(40, 2.35, 45.3, 7.65);
        d.OpenY(corner, down1, 7.65, 40, 45.3);
        d.OpenX(east, corner, 40, 2.35, 7.65);
        return (d, c, west, top2, east);
    }

    private static void PatrolLoop()
    {
        var (d, c, west, _, _) = LoopDungeon();
        var geo = d.Geo();
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        Check.True(route.Points.Count > 0, "a patrol");
        Check.Eq(route.RouteType, NavRouteType.Circular, "circular");
        int inside = route.Points.Count(p => X(p) > 4.5 && X(p) < 25.5 && Y(p) > 4.5 && Y(p) < 25.5);
        Check.Eq(inside, 0, "no point inside the big room (it is crossed door to door)");

        // Every doorway (narrow opening) of the loop is passed through near its centre.
        foreach (var (a, b) in d.Graph.Values.SelectMany(n => n.Neighbors.Select(m => (n.CellId, m))).Where(e => e.CellId < e.m))
        {
            if (!geo.TryGetDoorway(a, b, out var door) || door.IsWide) continue;
            double best = double.MaxValue;
            for (int i = 0; i < route.Points.Count; i++)
            {
                var p = route.Points[i]; var q = route.Points[(i + 1) % route.Points.Count];
                best = Math.Min(best, DistToSegment(door.X, door.Y, X(p), Y(p), X(q), Y(q)));
            }
            Check.True(best < 0.3, $"through the opening at ({door.X:F1}, {door.Y:F1}) within 0.3 m of its centre ({best:F2})");
        }

        var old = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null);
        int oldInside = old.Points.Count(p => X(p) > 3 && X(p) < 27 && Y(p) > 3 && Y(p) < 27);
        Check.True(oldInside >= 4, $"the old cell walk wandered through the room ({oldInside} points inside)");
    }

    private static void PatrolLooksIntoDeadEndRoom()
    {
        // Cut the east door: the room hangs off the west corridor by one doorway. A short ring
        // north of the top corridor keeps a cycle in the main route.
        var (d, c, west, top2, east) = LoopDungeon();
        d.Graph[east].Neighbors.Remove(c[2, 0]);
        d.Graph[c[2, 0]].Neighbors.Remove(east);
        uint ringA = d.Cell(15, 42.95, 20.3, 52.95), ringB = d.Cell(15, 52.95, 30, 58.25), ringC = d.Cell(24.7, 42.95, 30, 52.95);
        d.OpenY(top2, ringA, 42.95, 15, 20.3);
        d.OpenY(ringA, ringB, 52.95, 15, 20.3);
        d.OpenY(ringC, ringB, 52.95, 24.7, 30);
        d.OpenY(top2, ringC, 42.95, 24.7, 30);
        var geo = d.Geo();
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        bool looked = route.Points.Any(p => X(p) > 10 && X(p) < 20 && Y(p) > 10 && Y(p) < 20);
        Check.True(looked, "the patrol walks into the middle of the dead-end room");
    }

    private static void CeilingIsNotFloor()
    {
        Check.True(DungeonGeometry.IsUpwardFloor(Rect(0, 0, 10, 10, 0)), "counter-clockwise from above: a floor");
        Check.False(DungeonGeometry.IsUpwardFloor(Rect(0, 0, 10, 10, 4).Reverse().ToArray()), "clockwise from above: a ceiling");

        var d = new Dungeon();
        uint a = d.Cell(0, 0, 10, 10);
        d.Floors.Clear();
        d.Floors.Add((a, Rect(0, 0, 10, 10, 3.8).Reverse().ToArray()));   // only its ceiling is known
        var geo = d.Geo();
        Check.False(geo.HasFloor(a), "a cell with only a ceiling has no floor data");
        Check.True(geo.OnFloorOfAny(new[] { a }, 5, 5, 0), "so nothing is checked against it");
    }

    private static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + dx * t - px, cy = ay + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }
}

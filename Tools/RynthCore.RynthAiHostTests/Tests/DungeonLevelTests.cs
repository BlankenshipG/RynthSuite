using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The dungeon map's own routes over levels (2026-11-08, Silentkelpie: "the route is not taking into account for
// elevation or pits to jump in it should build in auto jumps to go down in levels"): a drop down is walked to its
// edge and jumped; the patrol, once round a level, goes down to the biggest part not patrolled yet.
internal static class DungeonLevelTests
{
    public static void Register(Runner r)
    {
        r.Add("dungeon levels: a route down a drop jumps at its edge, facing the way down", RouteJumps);
        r.Add("dungeon levels: the patrol's way down goes to the biggest part not patrolled yet", Descent);
    }

    private static uint _next;
    private static uint Cell(Dictionary<uint, DungeonNavNode> g, double x, double y, double z)
    {
        uint id = 0x7E7E0100 + _next++;
        g[id] = new DungeonNavNode { CellId = id, EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = (float)z };
        return id;
    }
    private static void Link(Dictionary<uint, DungeonNavNode> g, uint a, uint b) { g[a].Neighbors.Add(b); g[b].Neighbors.Add(a); }

    private static void RouteJumps()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        // Upper floor at 10 (west), a pit 6 m east and 17 m down, the lower floor on east of it.
        uint a = Cell(g, 100, 100, 10), b = Cell(g, 106, 100, -7), c = Cell(g, 116, 100, -7);
        Link(g, a, b); Link(g, b, c);
        var path = DungeonPathfinder.FindPath(a, c, g, null, DungeonPathfinder.PlannedMaxDropZ);
        Check.Eq(path.Count, 3, "the way down the pit");
        var route = DungeonPathfinder.BuildDropRoute(path, g, null, null, g[a].NS, g[a].EW, 10, g[c].NS, g[c].EW, -7, out int drops);
        Check.Eq(drops, 1, "one drop");
        int j = route.Points.FindIndex(p => p.Type == NavPointType.Jump);
        Check.True(j >= 0 && j < route.Points.Count - 1, "a Jump point before the end");
        if (j >= 0)
        {
            NavPoint jp = route.Points[j];
            Check.True(Math.Abs(jp.JumpHeading - 90.0) < 1.0, "facing east, the way down: " + jp.JumpHeading.ToString("F1"));
            Check.True(Math.Abs(jp.Z * 240.0 - 10.0) < 0.1, "the jump is on the upper floor");
            Check.True(Math.Abs(route.Points[j + 1].Z * 240.0 + 7.0) < 0.1, "then where you land, below");
        }
        Check.True(route.RouteType == NavRouteType.Once && Math.Abs(route.Points[^1].NS - g[c].NS) < 1e-6, "a Once route to the goal");
        Check.True(DungeonPathfinder.FindPath(c, a, g, null, DungeonPathfinder.PlannedMaxDropZ).Count == 0, "never back up the pit");
    }

    private static void Descent()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        // Level 1 at 20: three cells. A small pit (2 cells) off its west end, a big lower level (4 cells) off its east end.
        uint u1 = Cell(g, 300, 300, 20), u2 = Cell(g, 310, 300, 20), u3 = Cell(g, 320, 300, 20);
        Link(g, u1, u2); Link(g, u2, u3);
        uint s1 = Cell(g, 296, 300, 8), s2 = Cell(g, 286, 300, 8);
        Link(g, u1, s1); Link(g, s1, s2);
        uint l1 = Cell(g, 324, 300, 5), l2 = Cell(g, 334, 300, 5), l3 = Cell(g, 344, 300, 5), l4 = Cell(g, 354, 300, 5);
        Link(g, u3, l1); Link(g, l1, l2); Link(g, l2, l3); Link(g, l3, l4);

        var level = DungeonPathfinder.WalkableFrom(g, u1);
        Check.True(level.SetEquals(new[] { u1, u2, u3 }), "the patrol's level: the three upper cells");
        var path = DungeonPathfinder.DescentPath(g, u1, level, null, out var below);
        Check.True(path != null && path[^1] == l1 && path[^2] == u3, "down to the biggest part (the east level): " + (path == null ? "none" : string.Join(",", path.Select(c => (c & 0xFF).ToString()))));
        Check.Eq(below.Count, 4, "its four cells");
        var seen = new HashSet<uint>(level);
        seen.UnionWith(below);
        var next = DungeonPathfinder.DescentPath(g, u1, seen, null, out var below2);
        Check.True(next != null && next[^1] == s1 && below2.Count == 2, "that one patrolled: the pit next");
        seen.UnionWith(below2);
        Check.True(DungeonPathfinder.DescentPath(g, u1, seen, null, out _) == null, "all patrolled: no way down left");
    }
}

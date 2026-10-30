using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// RynthNav's dungeon walks through RynthAi's pathfinder, v2 (2026-10-30): a drop down a ledge is a
// one-way step (never up, no higher than allowed), the walk split at its edge; cells near a locked
// door are kept out of.
internal static class DungeonDropTests
{
    public static void Register(Runner r)
    {
        r.Add("dungeon path v2: a drop down is taken only when allowed and only down, its edge marked", DropsOneWay);
        r.Add("dungeon path v2: an avoid point (a locked door) sends the walk the other way round", AvoidsLockedDoor);
    }

    private static uint _next;
    private static uint Cell(Dictionary<uint, DungeonNavNode> g, double x, double y, double z)
    {
        uint id = 0x7D7D0100 + _next++;
        g[id] = new DungeonNavNode { CellId = id, EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = (float)z };
        return id;
    }
    private static void Link(Dictionary<uint, DungeonNavNode> g, uint a, uint b) { g[a].Neighbors.Add(b); g[b].Neighbors.Add(a); }

    private static void DropsOneWay()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        uint a = Cell(g, 100, 100, 10), b = Cell(g, 106, 100, 2), c = Cell(g, 116, 100, 2);
        Link(g, a, b); Link(g, b, c);

        Check.True(DungeonPathfinder.PluginWalk(g, null, 100, 100, 10, 116, 100, 2, 0, null, out int r0) == null
                   && r0 == RynthAiDungeonPathApiV1.ResultNoPath, "no drops allowed: no way down");
        var walk = DungeonPathfinder.PluginWalk(g, null, 100, 100, 10, 116, 100, 2, 10, null, out int r1);
        Check.True(walk != null && r1 == RynthAiDungeonPathApiV1.ResultOk, "drops up to 10: the way down");
        if (walk != null)
        {
            int drops = walk.Count(p => p.Kind == RynthAiDungeonPathApiV1.KindDrop);
            Check.Eq(drops, 1, "one drop edge");
            int i = walk.FindIndex(p => p.Kind == RynthAiDungeonPathApiV1.KindDrop);
            Check.True(i > 0 && i < walk.Count - 1, "the edge is inside the walk");
            if (i > 0 && i < walk.Count - 1)
            {
                Check.True(Math.Abs(walk[i].Z - 10) < 0.01, "the edge is on the upper level");
                Check.True(Math.Abs(walk[i + 1].Z - 2) < 0.01 && walk[i + 1].X > walk[i].X, "the landing is below, on past the edge");
            }
            Check.True(Math.Abs(walk[^1].X - 116) < 0.01 && Math.Abs(walk[^1].Z - 2) < 0.01, "ends at the goal");
        }
        Check.True(DungeonPathfinder.PluginWalk(g, null, 100, 100, 10, 116, 100, 2, 5, null, out _) == null, "an 8 m drop with drops up to 5: no way");
        Check.True(DungeonPathfinder.PluginWalk(g, null, 116, 100, 2, 100, 100, 10, 10, null, out _) == null, "never up a drop");
    }

    private static void AvoidsLockedDoor()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        uint a = Cell(g, 200, 200, 0), b = Cell(g, 210, 200, 0), c = Cell(g, 220, 200, 0);
        uint d1 = Cell(g, 200, 215, 0), d2 = Cell(g, 210, 215, 0), d3 = Cell(g, 220, 215, 0);
        Link(g, a, b); Link(g, b, c);
        Link(g, a, d1); Link(g, d1, d2); Link(g, d2, d3); Link(g, d3, c);

        var straight = DungeonPathfinder.PluginWalk(g, null, 200, 200, 0, 220, 200, 0, 0, null, out _);
        Check.True(straight != null && straight.All(p => p.Y < 205), "the short way through the middle cell");
        var round = DungeonPathfinder.PluginWalk(g, null, 200, 200, 0, 220, 200, 0, 0, new[] { (210.0, 200.0, 0.0) }, out int r);
        Check.True(round != null && r == RynthAiDungeonPathApiV1.ResultOk && round.Any(p => p.Y > 210), "a locked door in the middle cell: round by the north");
    }
}

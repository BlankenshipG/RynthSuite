using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.RouteTests;

// Dungeon walks through doors, locks, jumps, levels and drops (2026-10-30): which closed door
// stands across the walk, which pack items unlock one, the walker's jump before it gives up, a
// point on another level not counting as reached, and a drop's edge passed on the way down.
internal static partial class Program
{
    private static void RunWalkDoors()
    {
        Run(nameof(DoorAcrossTheWalk), DoorAcrossTheWalk);
        Run(nameof(UnlockersKeysThenPicks), UnlockersKeysThenPicks);
        Run(nameof(WalkerJumpsBeforeGivingUp), WalkerJumpsBeforeGivingUp);
        Run(nameof(WalkerKeepsToItsLevel), WalkerKeepsToItsLevel);
        Run(nameof(WalkerDropsOffALedge), WalkerDropsOffALedge);
        Run(nameof(DungeonWayCarriesDrops), DungeonWayCarriesDrops);
    }

    private static TownNetPoint P(double x, double y, double z = 0, bool drop = false) => new() { X = x, Y = y, Z = z, Drop = drop };

    private static void DoorAcrossTheWalk()
    {
        // From (0,0) north 6 m, then east 6 m.
        var ahead = new List<TownNetPoint> { P(0, 6), P(6, 6) };
        Near(WalkDoors.AlongWalk(0, 0, 0, ahead, false, 0.3, 4, 0), 4, 0.01, "a door in the corridor 4 m ahead");
        Near(WalkDoors.AlongWalk(0, 0, 0, ahead, false, 3, 6.5, 0), 9, 0.01, "a door round the corner, 9 m along the walk");
        Check(WalkDoors.AlongWalk(0, 0, 0, ahead, false, 3, 3, 0) < 0, "a door 3 m off to the side isn't across the walk");
        Check(WalkDoors.AlongWalk(0, 0, 0, new List<TownNetPoint> { P(0, 30) }, false, 0, 20, 0) < 0, "a door 20 m ahead isn't looked at yet");
        Check(WalkDoors.AlongWalk(0, 0, 0, ahead, true, 0, 4, -8) < 0, "a door on the level below isn't across a walk over levels");
        Check(WalkDoors.AlongWalk(0, 0, 0, ahead, false, 0, 4, -8) >= 0, "on a one-level walk heights don't count");
    }

    private static void UnlockersKeysThenPicks()
    {
        Check(WalkDoors.UnlockerOf("Key to the Rare Chest") == WalkDoors.Unlocker.Key, "a key");
        Check(WalkDoors.UnlockerOf("Keyring") == WalkDoors.Unlocker.Key, "a keyring");
        Check(WalkDoors.UnlockerOf("Reliable Lockpick") == WalkDoors.Unlocker.Lockpick, "a lockpick");
        Check(WalkDoors.UnlockerOf("Monkey Paw") == WalkDoors.Unlocker.None, "Monkey isn't a key");
        Check(WalkDoors.UnlockerOf("Keystone Fragment") == WalkDoors.Unlocker.None, "Keystone isn't a key");
        var order = WalkDoors.TryOrder(new[] { (1u, "Plain Lockpick"), (2u, "Pyreal"), (3u, "Brass Key"), (4u, "Good Lock Pick") });
        Check(order.Count == 3 && order[0].Id == 3 && order[1].Id == 1 && order[2].Id == 4, "keys first, then lockpicks; other items left out");
        Check(WalkDoors.IsLockedText("The Iron Door is locked!") && WalkDoors.IsLockedText("The door is locked."), "the server's locked line");
        Check(!WalkDoors.IsLockedText("Bob says, \"The door is locked!\""), "a player quoting it isn't");
    }

    private static void WalkerJumpsBeforeGivingUp()
    {
        var w = new HubWalker(new List<TownNetPoint> { P(0, 0), P(0, 20) }, -100_000, 0);
        Check(w.Step(0, 0, 100, out _, out _) == HubWalker.Act.Run, "runs at first");
        var acts = new List<HubWalker.Act>();
        for (long t = 200; t <= 9000; t += 100) { var a = w.Step(0, 0, t, out _, out _); if (a != HubWalker.Act.Run) acts.Add(a); if (a == HubWalker.Act.Stop) break; }
        Check(acts.Count == 3 && acts[0] == HubWalker.Act.Jump && acts[1] == HubWalker.Act.Jump && acts[2] == HubWalker.Act.Stop,
            $"stuck: jumps twice, then stops ({string.Join(",", acts)})");
        var w2 = new HubWalker(new List<TownNetPoint> { P(0, 0), P(0, 20) }, -100_000, 0);
        w2.Step(0, 0, 100, out _, out _);
        w2.Hold(2000, 2000);   // a door opening: not stuck
        Check(w2.Step(0, 0, 3500, out _, out _) == HubWalker.Act.Run, "holding for a door isn't being stuck");
    }

    private static void WalkerKeepsToItsLevel()
    {
        // Up a ramp that turns back over itself: the second turn is right above the start.
        var way = new List<TownNetPoint> { P(0, 0, 0), P(0, 10, 3), P(1, 10, 3), P(1, 0, 6), P(1, -5, 6) };
        var w = new HubWalker(way, -100_000, 0);
        Check(w.Levels, "a walk over levels");
        w.Step(0, 9.9, 3, 100, out _, out _);   // at the first turn
        w.Step(1, 9.9, 3, 200, out _, out _);   // round it
        int before = w.Index;
        // Under the upper turn (1,0,6) at the bottom's height: not reached.
        w.Step(1, 0.2, 0, 300, out _, out _);
        Check(w.Index == before, "a point right above isn't reached from the level below");
        w.Step(1, 0.2, 5.5, 400, out _, out _);
        Check(w.Index > before, "reached on its own level");
        var flat = new HubWalker(new List<TownNetPoint> { P(0, 0, 0), P(0, 5, 0.5), P(0, 10, 0) }, -100_000, 0);
        Check(!flat.Levels, "a one-level walk ignores heights");
    }

    private static void WalkerDropsOffALedge()
    {
        // Walk to a ledge's edge at 10 m high, land 1.5 m on at 2 m, walk on.
        var way = new List<TownNetPoint> { P(0, 0, 10), P(0, 5, 10, drop: true), P(0, 6.5, 2), P(0, 12, 2) };
        var w = new HubWalker(way, -100_000, 0);
        w.Step(0, 4.8, 10, 100, out _, out _);
        Check(w.Index >= 2, "the edge is reached on the upper level");
        int i = w.Index;
        w.Step(0, 6.4, 2.2, 200, out _, out _);   // landed below
        Check(w.Index > i, "the landing is reached on the lower level");
        var w2 = new HubWalker(way, -100_000, 0);
        w2.Step(0, 5.2, 4, 100, out _, out _);   // falling past the edge
        Check(w2.Index >= 2, "falling below the edge counts as past it");
    }

    private static void DungeonWayCarriesDrops()
    {
        var dw = new DungeonWay(new double[] { 0, 0, 10, 0, 5, 10, 0, 6.5, 2 }, new[] { false, true, false });
        TownNetPoint[] pts = dw.ToPoints(0x01230100);
        Check(pts.Length == 3 && !pts[0].Drop && pts[1].Drop && !pts[2].Drop && pts[2].Z == 2, "the drop's edge is marked on the walker's point");
        Check(dw.HasDrop && !new DungeonWay(new double[] { 0, 0, 0, 1, 1, 0 }).HasDrop, "HasDrop");
    }
}

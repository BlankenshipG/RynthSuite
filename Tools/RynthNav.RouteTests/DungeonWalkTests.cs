using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Walks inside dungeons (2026-10-29): the way walked from a landing to a dungeon portal is kept with
// that portal's landing, and TownNet.Merge makes the dungeon a hub the planner routes through, one
// dungeon into the next too.
internal static partial class Program
{
    private static void RunDungeonWalks()
    {
        Run(nameof(TrailCutsLoops), TrailCutsLoops);
        Run(nameof(DungeonWalksRoundTrip), DungeonWalksRoundTrip);
        Run(nameof(DungeonWalkKeepsTheShorter), DungeonWalkKeepsTheShorter);
        Run(nameof(LearnedDungeonRoutesATrip), LearnedDungeonRoutesATrip);
        Run(nameof(LearnedDungeonsChain), LearnedDungeonsChain);
        Run(nameof(LearnedDungeonsArePerServer), LearnedDungeonsArePerServer);
    }

    private const uint HubCell = 0x01D90101, VaultCell = 0x02AA0105;

    private static DungeonWalk Walk(uint cell, double ax, double ay, string exit, double ex, double ey, params double[] mid)
    {
        var pts = new List<double> { ax, ay, 0 };
        for (int i = 0; i + 1 < mid.Length; i += 2) { pts.Add(mid[i]); pts.Add(mid[i + 1]); pts.Add(0); }
        pts.Add(ex); pts.Add(ey); pts.Add(0);
        return new DungeonWalk
        {
            Landblock = cell >> 16, ArrivalCell = cell, ArrivalX = ax, ArrivalY = ay, FromLanding = true,
            ExitName = exit, ExitCell = cell, ExitX = ex, ExitY = ey, Points = pts.ToArray(), LearnedUtc = T0,
        };
    }

    /// <summary>Leaftide Hub: a map portal at 0N 0.05E into dungeon 01D9, landing (10, 10); its "Portal to Far Town" at (30, 10) lands at 40N 40E.</summary>
    private static CustomPortalStore HubStore(string server = Ilt)
    {
        var s = new CustomPortalStore();
        s.Learned(server, "Portal to Leaftide Hub", 0, 0xA9B40001, 0.0, 0.05, false, 0, 0, HubCell, true, 10, 10, 0, null, T0);
        s.LearnedInDungeon(server, "Portal to Far Town", 0, HubCell, 30, 10, 0, true, 40.0, 40.0, 0x7D640012, false, 0, 0, 0, T0);
        Check(s.AddWalk(server, Walk(HubCell, 10, 10, "Portal to Far Town", 30, 10, 20, 12)), "the walk is kept");
        return s;
    }

    private static void TrailCutsLoops()
    {
        var t = new DungeonTrail();
        t.Start(HubCell, 0, 0, 0, fromLanding: true);
        for (int x = 1; x <= 6; x++) t.Add(x, 0, 0);
        Check(t.Count == 7, "out along a corridor: a point a metre");
        t.Add(6, 3, 0); t.Add(3, 3, 0);
        t.Add(2.2, 0.3, 0);   // back beside the second point: the loop goes
        Check(t.Count == 2, $"coming back cuts the loop (left {t.Count})");
        t.Add(1.5, 0.2, 0);
        Check(t.Count == 2, "less than a metre on: nothing added");
        double[] to = t.To(5, 0, 0);
        Check(to.Length == 9 && to[6] == 5, "the portal's centre ends it");
        t.Add(0.1, 0.1, 0);
        Check(t.Count == 2, "beside the first point: within a step of the last one, so nothing changes");
    }

    private static void DungeonWalksRoundTrip()
    {
        CustomPortalStore s = HubStore();
        s.SawInDungeon(Ilt, "Untried Portal", 5, HubCell, 50, 50, 1.5, T0);
        CustomPortalStore back = CustomPortalStore.Parse(s.Serialize(), s.SerializeWalks());
        Check(back.For(Ilt).Count == 3, "portals come back");
        CustomPortal entry = back.For(Ilt)[0], exit = back.For(Ilt)[1], untried = back.For(Ilt)[2];
        Check(entry.OnMap && entry.DestKnown && !entry.DestOnMap && entry.DestHasPos && entry.DestX == 10, "the map portal's dungeon landing spot");
        Check(!exit.OnMap && exit.X == 30 && exit.DestOnMap && exit.DestNs == 40, "the dungeon portal: where it stands, where it lands");
        Check(!untried.OnMap && !untried.DestKnown && untried.Z == 1.5, "an untried dungeon portal");
        Check(back.WalksFor(Ilt).Count == 1 && back.WalksFor(Ilt)[0].Count == 3, "the walk and its points");
        Near(back.WalksFor(Ilt)[0].Length, s.WalksFor(Ilt)[0].Length, 0.05, "its length");
        Check(CustomPortalStore.Parse("x\ty\t1\t0\t1\t2\t-\n").For("x").Count == 1, "an old line (no dungeon fields) still reads");
    }

    private static void DungeonWalkKeepsTheShorter()
    {
        CustomPortalStore s = HubStore();
        Check(!s.AddWalk(Ilt, Walk(HubCell, 10.5, 10, "Portal to Far Town", 30, 10, 20, 30, 25, 30)), "a longer walk between the same ends isn't kept");
        Check(s.AddWalk(Ilt, Walk(HubCell, 10.5, 10, "Portal to Far Town", 30, 10)), "a shorter one replaces it");
        Check(s.WalksFor(Ilt).Count == 1 && s.WalksFor(Ilt)[0].Count == 2, "one walk, the straight one");
        Check(s.AddWalk(Ilt, Walk(HubCell, 60, 60, "Portal to Far Town", 30, 10)), "from another arrival: a second walk");
        Check(s.WalksFor(Ilt).Count == 2, "two walks");
    }

    private static void LearnedDungeonRoutesATrip()
    {
        CustomPortalStore s = HubStore();
        TownNet net = TownNet.Merge(null, s, Ilt);
        var (e, x, w) = net.LearnedCounts();
        Check(e == 1 && x == 1 && w == 1, $"one entry, one exit, one walk ({e}/{x}/{w})");
        Check(net.Contains(0x01D9), "the dungeon is part of the hub");
        Check(net.Exits[0].Hub == "Portal to Leaftide Hub", "named by the portal into it");
        HubLinks hub = net.Links(0, false, null);
        Check(hub.Entries.Count == 1 && hub.Exits.Count == 1, "the planner sees them");
        List<RouteStep> steps = PortalRoute.Plan(BuiltIn(), null, hub, 0, 0, 40.1, 40.0, out double est, out int used);
        Check(used == 2 && steps.Count == 3 && steps[0].UsePortal && steps[1].IsHub && !steps[2].UsePortal,
            $"walk to the entry, through the dungeon, walk on ({string.Join(" / ", steps.ConvertAll(z => z.Label))})");
        Check(est < NavCoords.Distance(0, 0, 40.1, 40) / 4, "far cheaper than walking");

        var trip = new PlannedTrip(1, steps, "goal", 40.1, 40.0, true, WorldPt.FromMap(0, 0), 0xA9B40001, null);
        List<WorldPt>? inside = trip.HubWalk(1, net, out string why);
        Check(inside != null && inside.Count == 3, $"the trip's walk inside is the learned one ({why})");
        Check(PlannedTrip.HubExitPoint(net, steps[1]) is WorldPt p && Math.Abs(p.X - (0x01 * 192.0 + 30)) < 1e-6, "its exit portal's spot");

        Check(TownNet.Merge(null, HubStore(), "").Exits.Count == 0, "no server: nothing learned is added");
        CustomPortalStore noWalk = new();
        noWalk.Learned(Ilt, "Portal to Leaftide Hub", 0, 0xA9B40001, 0.0, 0.05, false, 0, 0, HubCell, true, 10, 10, 0, null, T0);
        Check(TownNet.Merge(null, noWalk, Ilt).Entries.Count == 0, "an entry with nothing walked inside isn't routed");
    }

    private static void LearnedDungeonsChain()
    {
        CustomPortalStore s = HubStore();
        // The hub's "Portal to Deep Vault" at (10, 30) lands in dungeon 02AA at (5, 5); its "Vault Exit" at (25, 5) lands at 40S 40W.
        s.LearnedInDungeon(Ilt, "Portal to Deep Vault", 0, HubCell, 10, 30, 0, false, 0, 0, VaultCell, true, 5, 5, 0, T0);
        s.AddWalk(Ilt, Walk(HubCell, 10, 10, "Portal to Deep Vault", 10, 30));
        s.LearnedInDungeon(Ilt, "Vault Exit", 0, VaultCell, 25, 5, 0, true, -40.0, -40.0, 0x2A2A0012, false, 0, 0, 0, T0);
        s.AddWalk(Ilt, Walk(VaultCell, 5, 5, "Vault Exit", 25, 5));
        TownNet net = TownNet.Merge(null, s, Ilt);
        int deep = net.Exits.FindIndex(z => z.Name == "Portal to Deep Vault");
        Check(deep >= 0 && net.Exits[deep].DestArrival >= 0, "the portal into the vault lands at the vault's arrival");
        HubLinks hub = net.Links(0, false, null);
        Check(hub.Exits.Count == 3, "three exits planned with");
        List<RouteStep> steps = PortalRoute.Plan(BuiltIn(), null, hub, 0, 0, -40.1, -40.0, out _, out int used);
        Check(steps.Count == 4 && steps[0].UsePortal && steps[1].IsHub && steps[2].IsHub && double.IsNaN(steps[1].LandNs),
            $"entry, the hub to the vault, the vault out, walk ({string.Join(" / ", steps.ConvertAll(z => z.Label))})");
        Check(used == 3, "three teleports");
        Check(net.Exits[steps[1].HubExit].Name == "Portal to Deep Vault" && net.Exits[steps[2].HubExit].Name == "Vault Exit", "in that order");
        var trip = new PlannedTrip(2, steps, "goal", -40.1, -40.0, true, WorldPt.FromMap(0, 0), 0xA9B40001, null);
        Check(trip.HubWalk(2, net, out string why) is { Count: 2 }, $"the chained step's walk starts at the vault's arrival ({why})");
    }

    private static void LearnedDungeonsArePerServer()
    {
        CustomPortalStore s = HubStore(Ilt);
        Check(TownNet.Merge(null, s, Other).Exits.Count == 0, "another server never routes through them");
        TownNet t = TownNet.Merge(null, s, "infinite leaftide");
        Check(t.Exits.Count == 1, "the server matched without case");
    }
}

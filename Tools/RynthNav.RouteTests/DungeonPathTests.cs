using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Planned dungeon walks (2026-10-29): with a dungeon pathfinder (RynthAi's, from the dats) the route
// hub gets a planned walk from each learned arrival to each portal of that dungeon whose landing is
// known, so a route can use a portal nobody walked to from there.
internal static partial class Program
{
    private static void RunDungeonPaths()
    {
        Run(nameof(PathfinderFillsMissingWalks), PathfinderFillsMissingWalks);
        Run(nameof(PathfinderMakesEntriesAndChains), PathfinderMakesEntriesAndChains);
        Run(nameof(TripWalkAfterARecallIntoADungeon), TripWalkAfterARecallIntoADungeon);
    }

    /// <summary>A fake pathfinder: a dog-leg through (ax, by) - the walls are the test's business.</summary>
    private static int _pathCalls;
    private static double[]? DogLeg(uint lb, double ax, double ay, double az, double bx, double by, double bz)
    {
        _pathCalls++;
        return new[] { ax, ay, az, ax, by, az, bx, by, bz };
    }

    private static void PathfinderFillsMissingWalks()
    {
        CustomPortalStore s = MineStore();
        // A second mine portal, taken once from somewhere else (no walk from the arrival): Dark Isle.
        s.LearnedInDungeon(Ilt, "Dark Isle", 0, 0x01C901F2, 116.4, -32.6, -6.06, true, 84.7, 58.2, 0xC8E9000B, false, 0, 0, 0, T0);
        TownNet without = TownNet.Merge(null, s, Ilt, MineAtlas);
        Check(!without.Exits.Exists(x => x.Name == "Dark Isle"), "without the pathfinder: no walk to it, so not routed");
        _pathCalls = 0;
        TownNet net = TownNet.Merge(null, s, Ilt, MineAtlas, DogLeg);
        int dark = net.Exits.FindIndex(x => x.Name == "Dark Isle");
        int arrival = net.LearnedArrivalByCell(0, MineArrivalCell);
        TownNetWalk? w = dark >= 0 ? net.Walk(arrival, dark) : null;
        Check(w != null && w.Planned && w.Points.Length == 3, "a planned walk from the arrival to the Dark Isle portal");
        Check(net.Walk(arrival, net.Exits.FindIndex(x => x.Name == "Snowy Valley Portal")) is { Planned: false }, "a walked walk stays the walked one");
        Check(net.PlannedWalks() == 1 && _pathCalls == 1, "only the missing pair is asked for");
        HubLinks hub = net.Links(0, false, null);
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), null, hub, 34.95, 54.5, 84.8, 58.2, out _, out int used);
        Check(used == 2 && steps[1].IsHub && net.Exits[steps[1].HubExit].Name == "Dark Isle", "a route through the mine to Dark Isle by the planned walk");
    }

    private static void PathfinderMakesEntriesAndChains()
    {
        CustomPortalStore s = MineStore();
        // A map portal into the mine landing 40 m from the walked arrival; a mine portal into vault 02AA (landing spot known, never walked there).
        s.Learned(Ilt, "Side Door", 0, 0xA9B40001, 10.0, 10.0, false, 0, 0, 0x01C90240, true, 40, -60, 0, null, T0);
        s.LearnedInDungeon(Ilt, "Vault Door", 0, 0x01C90241, 90, -10, 0, false, 0, 0, 0x02AA0105, true, 5, 5, 0, T0);
        s.LearnedInDungeon(Ilt, "Vault Exit", 0, 0x02AA0106, 25, 5, 0, true, -40.0, -40.0, 0x2A2A0012, false, 0, 0, 0, T0);
        TownNet net = TownNet.Merge(null, s, Ilt, MineAtlas, DogLeg);
        Check(net.Entries.Exists(e => e.Name == "Side Door" && e.Arrival >= 0), "the side door is an entry at a new arrival");
        int vault = net.Exits.FindIndex(x => x.Name == "Vault Door");
        Check(vault >= 0 && net.Exits[vault].DestArrival >= 0, "the vault door chains to an arrival in the vault");
        Check(net.Walk(net.Exits[vault].DestArrival, net.Exits.FindIndex(x => x.Name == "Vault Exit")) is { Planned: true }, "and on to the vault's exit, planned");
        HubLinks hub = net.Links(0, false, null);
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), null, hub, 10.05, 10.0, -40.1, -40.0, out _, out int used);
        Check(used == 3 && steps[0].UsePortal && steps[1].IsHub && steps[2].IsHub, $"side door, through the mine, through the vault ({string.Join(" / ", steps.ConvertAll(z => z.Label))})");
    }

    private static void TripWalkAfterARecallIntoADungeon()
    {
        TownNet net = TownNet.Merge(null, MineStore(), Ilt, MineAtlas);
        int arrival = net.LearnedArrivalByCell(0, MineArrivalCell);
        var gem = new RecallOption { Name = "Abandoned Mines Portal Sending Gem (gem)", Command = "/mt usei x", CastSeconds = 3, ToArrival = arrival };
        HubLinks hub = net.Links(0, false, null);
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), new List<RecallLink> { gem.ToLink() }, hub, -60, 60, 85.4, -8.8, out _, out _);
        var trip = new PlannedTrip(3, steps, "goal", 85.4, -8.8, true, WorldPt.FromMap(-60, 60), 0x11110001,
            new List<TripRecall> { new(gem.Name, 0, gem.Command, 3, arrival) });
        Check(steps[0].UseRecall && steps[1].IsHub, "gem then the mine");
        Check(trip.HubWalk(1, net, out string why) is { Count: 3 }, $"the trip knows the walk from where the gem lands ({why})");
    }
}

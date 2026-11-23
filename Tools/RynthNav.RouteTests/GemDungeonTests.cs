using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// A gem or recall into a dungeon RynthNav learned walks in (2026-10-29): it lands at that dungeon's
// arrival and the route goes on through the dungeon's portals; standing in one, the plan walks to
// its portals. The Abandoned Mine (01C9) case from Infinite Leaftide's logs.
internal static partial class Program
{
    private static void RunGemDungeons()
    {
        Run(nameof(GemNameFindsTheDungeon), GemNameFindsTheDungeon);
        Run(nameof(GemIntoLearnedDungeonRoutesOut), GemIntoLearnedDungeonRoutesOut);
        Run(nameof(StartInLearnedDungeonWalksOut), StartInLearnedDungeonWalksOut);
        Run(nameof(AtlasPortalIntoLearnedDungeonIsAnEntry), AtlasPortalIntoLearnedDungeonIsAnEntry);
        Run(nameof(RecallSpotKeepsDungeonPosition), RecallSpotKeepsDungeonPosition);
    }

    private const uint MineArrivalCell = 0x01C9022D;

    private static readonly Atlas MineAtlas = Atlas.Parse(@"{""locations"":[
        {""name"":""Abandoned Mine"",""type"":""Dungeon"",""ns"":34.9,""ew"":54.51,""cell"":""C3AB0000"",""place"":""outdoor"",""lb"":""01C9"",""id"":296},
        {""name"":""Abandoned Mine"",""type"":""Portal"",""wcid"":1595,""cell"":""C3AB0000"",""place"":""outdoor"",""ns"":34.9,""ew"":54.51,""dest"":{""cell"":""01C9022D"",""place"":""dungeon"",""name"":""Abandoned Mine""},""id"":4242},
        {""name"":""Timaru"",""type"":""Town"",""ns"":-47.0,""ew"":-24.0,""cell"":""1A2B0000"",""place"":""outdoor"",""id"":9}
        ],""recalls"":[]}");

    /// <summary>01C9 learned: from its arrival (72.9, -30.2) walks to "Snowy Valley Portal" (lands 85.3N 8.8W) and "Vaulted Dungeons" (into 596A).</summary>
    private static CustomPortalStore MineStore()
    {
        var s = new CustomPortalStore();
        s.LearnedInDungeon(Ilt, "Snowy Valley Portal", 0, 0x01C901F2, 116.4, -27.5, -6.06, true, 85.3, -8.8, 0x11110001, false, 0, 0, 0, T0);
        s.AddWalk(Ilt, new DungeonWalk
        {
            Landblock = 0x01C9, ArrivalCell = MineArrivalCell, ArrivalX = 72.9, ArrivalY = -30.2, FromLanding = true,
            ExitName = "Snowy Valley Portal", ExitCell = 0x01C901F2, ExitX = 116.4, ExitY = -27.5, ExitZ = -6.06,
            Points = new double[] { 72.9, -30.2, 0, 100, -30, -6.06, 116.4, -27.5, -6.06 }, LearnedUtc = T0,
        });
        return s;
    }

    private static void GemNameFindsTheDungeon()
    {
        Check(GemPlanner.PlaceInName("Abandoned Mines Portal Sending Gem") == "Abandoned Mines", "the place in the name");
        Check(GemPlanner.PlaceInName("Unlimited Dark Island Portal Gem") == "Dark Island", "lead and trailing words dropped");
        Check(GemPlanner.Norm("Abandoned Mines") == GemPlanner.Norm("Abandoned Mine"), "Mines = Mine");
        Check(GemPlanner.GuessFromName("Abandoned Mines Portal Sending Gem", MineAtlas, out RecallSpot? s, out string what)
              && s is { OnMap: false, Cell: MineArrivalCell } && what == "Abandoned Mine", "the Abandoned Mine, landing where its portal does");
        Check(GemPlanner.GuessFromName("Timaru Portal Sending Gem", MineAtlas, out RecallSpot? t, out _) && t is { OnMap: true, Ns: -47.0 }, "a town by name");
        Check(!GemPlanner.GuessFromName("Rune of Dispel", MineAtlas, out _, out _), "nothing for a name that matches no place");
    }

    private static void GemIntoLearnedDungeonRoutesOut()
    {
        TownNet net = TownNet.Merge(null, MineStore(), Ilt, MineAtlas);
        int arrival = net.LearnedArrivalByCell(0, MineArrivalCell);
        Check(arrival >= 0, "the mine's arrival is learned");
        Check(net.Exits.Exists(x => x.Hub == "Abandoned Mine"), "the learned dungeon is named from the Atlas");
        ArrivalResolver of = (cell, hasPos, x, y, z) => hasPos ? net.LearnedArrival(0, cell >> 16, x, y, z) : net.LearnedArrivalByCell(0, cell);
        var notes = new List<string>();
        List<RecallOption> gems = GemPlanner.Build(new List<PackGem> { Gem(500, "Abandoned Mines Portal Sending Gem", 0) },
            MineAtlas, new RecallMemory(), "c", new GemMemory(), Ilt, 240, notes, of);
        Check(gems.Count == 1 && gems[0].ToArrival == arrival && gems[0].Via.StartsWith("guessed"), $"the gem lands at the mine's arrival ({string.Join("; ", notes)})");
        HubLinks hub = net.Links(0, false, null);
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), RecallPlanner.Links(gems), hub, -60, 60, 85.4, -8.8, out double est, out int used);
        Check(steps.Count == 3 && steps[0].UseRecall && steps[1].IsHub && net.Exits[steps[1].HubExit].Name == "Snowy Valley Portal",
            $"gem, then through the mine to the Snowy Valley Portal ({string.Join(" / ", steps.ConvertAll(z => z.Label))})");
        Check(used == 2 && est < NavCoords.Distance(-60, 60, 85.4, -8.8) / 10, "and far cheaper than walking");

        // Learned by using it (a landing with a position) beats the guess.
        var learned = new GemMemory();
        learned.Set(Ilt, 500, "Abandoned Mines Portal Sending Gem", new RecallSpot { OnMap = false, Cell = MineArrivalCell, HasPos = true, X = 73.1, Y = -30.0, Label = "a dungeon (landblock 01C9)" });
        List<RecallOption> g2 = GemPlanner.Build(new List<PackGem> { Gem(500, "Abandoned Mines Portal Sending Gem", 0) },
            MineAtlas, new RecallMemory(), "c", learned, Ilt, 240, null, of);
        Check(g2.Count == 1 && g2[0].Via == "learned" && g2[0].ToArrival == arrival, "learned landing in the dungeon");

        var none = GemPlanner.Build(new List<PackGem> { Gem(500, "Abandoned Mines Portal Sending Gem", 0) }, MineAtlas, new RecallMemory(), "c", new GemMemory(), Ilt, 240, notes, null);
        Check(none.Count == 0 && notes.Exists(n => n.Contains("way out")), "with no learned walks: left out, saying a way out is needed");
    }

    private static void StartInLearnedDungeonWalksOut()
    {
        TownNet net = TownNet.Merge(null, MineStore(), Ilt, MineAtlas);
        HubLinks hub = net.Links(0, false, null);
        var walks = new double[hub.Exits.Count];
        for (int i = 0; i < walks.Length; i++)
        {
            List<TownNetPoint>? way = net.WayTo(hub.Exits[i].Id, 74, -30, HubWalker.OffRouteUnits, out _);
            walks[i] = way == null ? double.NaN : TownNet.PathLength(way, 0);
        }
        TravelPlan.Result r = TravelPlan.Plan(new List<PortalLink>(), new List<RecallLink>(), hub, true, true, false,
            0, 0, 85.4, -8.8, null, null, "goal", walks);
        Check(r.Refusal.Length == 0 && r.Steps != null && r.Steps[0].IsHub, $"no recall needed: walk to the portal and take it ({r.Refusal})");
        TravelPlan.Result none = TravelPlan.Plan(new List<PortalLink>(), new List<RecallLink>(), hub, true, true, false,
            0, 0, 85.4, -8.8, null, null, "goal", null);
        Check(none.Refusal.Length > 0, "without the walks, still refused (the old behaviour)");
    }

    private static void AtlasPortalIntoLearnedDungeonIsAnEntry()
    {
        TownNet net = TownNet.Merge(null, MineStore(), Ilt, MineAtlas);
        Check(net.Entries.Exists(e => e.Name == "Abandoned Mine" && e.Learned && Math.Abs(e.Ns - 34.9) < 1e-9), "the world data's Abandoned Mine portal enters the learned mine");
        HubLinks hub = net.Links(0, false, null);
        List<RouteStep> steps = PortalRoute.Plan(new List<PortalLink>(), null, hub, 34.95, 54.5, 85.4, -8.8, out _, out int used);
        Check(used == 2 && steps[0].UsePortal && steps[1].IsHub, "walk into it, through the mine, out by Snowy Valley");
    }

    private static void RecallSpotKeepsDungeonPosition()
    {
        var m = new RecallMemory();
        m.Set("ILT|Kelp", RecallSlot.LastPortal, new RecallSpot { OnMap = false, Label = "a dungeon (landblock 01C9)", Cell = MineArrivalCell, HasPos = true, X = 72.9, Y = -30.2, Z = 0 });
        RecallSpot? back = RecallMemory.Parse(m.Serialize()).Get("ILT|Kelp", RecallSlot.LastPortal);
        Check(back is { OnMap: false, Cell: MineArrivalCell, HasPos: true, X: 72.9 }, "a dungeon landing keeps its cell and spot");
        TownNet net = TownNet.Merge(null, MineStore(), Ilt, MineAtlas);
        ArrivalResolver of = (cell, hasPos, x, y, z) => hasPos ? net.LearnedArrival(0, cell >> 16, x, y, z) : net.LearnedArrivalByCell(0, cell);
        var known = new HashSet<int> { RecallPlanner.PortalRecall };
        List<RecallOption> r = RecallPlanner.Build(MineAtlas, known, m, "ILT|Kelp", null, of);
        Check(r.Exists(o => o.SpellId == RecallPlanner.PortalRecall && o.ToArrival >= 0), "Portal Recall into the learned mine is routed");
    }
}

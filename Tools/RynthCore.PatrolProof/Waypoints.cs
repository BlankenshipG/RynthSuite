using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.PatrolProof;

// "waypoints": the patrol the plugin builds from given start cells, one line per waypoint with
// the rule that laid it (DungeonPathfinder.DescribePatrol, the same lines BuildDunPatrol logs as
// "[RynthAi] DunNav-Patrol wp ..."), and the hazard set the game would use:
//   dotnet run -c Release -- 6146 waypoints 61460100 61460325 [surface] [hzfile <file>] [hz 012E,0131] [summary] [tiny]
//     surface      add the Detector C cells (floor textures in HazardTextures.txt, as the game seeds them)
//     hzfile f     add the cells listed in a DungeonHazards/<lb>.txt file
//     hz a,b,...   add cells (low 16 bits hex, or full ids)
//     compare      for each start, the summary with none / surface / file+hz / all hazards
//     summary      summary lines only        tiny   only waypoints in rooms of <= 5 cells
// The start cell is handled as BuildDunPatrol does: a start in a hazard moves to NearestSafeCell.
internal static class Waypoints
{
    public static int Run(string[] args)
    {
        uint lb = 0x6146;
        string acDir = @"C:\Turbine\Asheron's Call";
        var starts = new List<uint>();
        bool surface = false, summaryOnly = false, tinyOnly = false, compare = false;
        var extra = new HashSet<uint>();
        var fileCells = new HashSet<uint>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "waypoints") continue;
            if (a == "surface") surface = true;
            else if (a == "summary") summaryOnly = true;
            else if (a == "tiny") tinyOnly = true;
            else if (a == "compare") compare = true;
            else if (a == "hzfile" && i + 1 < args.Length)
            {
                foreach (string raw in File.ReadAllLines(args[++i]))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l[0] == '#') continue;
                    if (uint.TryParse(l.Replace("0x", ""), NumberStyles.HexNumber, null, out uint c)) fileCells.Add(c);
                }
            }
            else if (a == "hz" && i + 1 < args.Length)
                foreach (string t in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    extra.Add(uint.Parse(t.Replace("0x", ""), NumberStyles.HexNumber));
            else if (a.Length <= 6 && uint.TryParse(a.Replace("0x", ""), NumberStyles.HexNumber, null, out uint v)) lb = v;
            else if (a.Length == 8 && uint.TryParse(a, NumberStyles.HexNumber, null, out uint s)) starts.Add(s);
            else acDir = a;
        }
        // Low-16 ids become full cell ids of this landblock.
        extra = extra.Select(c => c <= 0xFFFF ? (lb << 16) | c : c).ToHashSet();

        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("Geometry loader failed: " + loader.StatusMessage); return 1; }
        var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
        var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
        var surfaceCells = DungeonHazardSurfaces.ComputeHazardCells(lb, loader.CellDat, loader.PortalDat);
        Console.WriteLine($"Landblock 0x{lb:X4}: {graph.Count} cells; hazard textures {DungeonHazardSurfaces.HazardTextureCount} -> {surfaceCells.Count} Detector C cells: {Ids(surfaceCells)}");
        if (fileCells.Count > 0) Console.WriteLine($"  hzfile cells: {Ids(fileCells)}");
        if (extra.Count > 0) Console.WriteLine($"  hz cells: {Ids(extra)}");
        if (starts.Count == 0) starts.Add(graph.Keys.Min());

        var sets = new List<(string Name, HashSet<uint> Cells)>();
        var given = new HashSet<uint>(fileCells.Concat(extra));
        if (compare)
        {
            sets.Add(("none", new HashSet<uint>()));
            sets.Add(("surface", new HashSet<uint>(surfaceCells)));
            if (given.Count > 0) sets.Add(("file+hz", new HashSet<uint>(given)));
            if (given.Count > 0) sets.Add(("all", new HashSet<uint>(surfaceCells.Concat(given))));
        }
        else
        {
            var cells = new HashSet<uint>(given);
            if (surface) cells.UnionWith(surfaceCells);
            sets.Add((surface ? (given.Count > 0 ? "all" : "surface") : given.Count > 0 ? "file+hz" : "none", cells));
        }

        foreach (uint s0 in starts)
            foreach (var (name, cells) in sets)
            {
                uint start = s0;
                IReadOnlySet<uint> hazards = cells.Count > 0 ? cells : null;
                if (hazards != null && hazards.Contains(start))
                {
                    uint safe = DungeonPathfinder.NearestSafeCell(graph, start, hazards);
                    if (safe != 0) start = safe;
                }
                var log = new Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo>(ReferenceEqualityComparer.Instance);
                DungeonPathfinder.PointLog = log;
                NavRouteParser route;
                try { route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, geo); }
                finally { DungeonPathfinder.PointLog = null; }
                var lines = DungeonPathfinder.DescribePatrol(route, graph, geo, hazards, log);
                Console.WriteLine();
                Console.WriteLine($"== start {s0:X8}{(start != s0 ? $" (moved to safe {start:X8})" : "")}, hazards {name} ({cells.Count})");
                Console.WriteLine("   " + EnteredRooms(route, graph, geo, hazards, log));
                foreach (string l in lines)
                {
                    bool isSummary = l.StartsWith("wp summary", StringComparison.Ordinal) || l.StartsWith("wp main route", StringComparison.Ordinal);
                    if (summaryOnly && !isSummary) continue;
                    if (tinyOnly && !isSummary && !IsTiny(l)) continue;
                    Console.WriteLine("   " + l);
                }
            }
        return 0;
    }

    // "room N (k cells)" with k <= 5.
    private static bool IsTiny(string line)
    {
        int at = line.IndexOf(" room ", StringComparison.Ordinal);
        if (at < 0) return false;
        int open = line.IndexOf('(', at), sp = line.IndexOf(" cells)", at, StringComparison.Ordinal);
        return open > 0 && sp > open && int.TryParse(line.AsSpan(open + 1, sp - open - 1), out int n) && n > 0 && n <= 5;
    }

    // Rooms with at least one waypoint not laid by a look-in, by size.
    private static string EnteredRooms(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                       IReadOnlySet<uint> hazards, Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo> log)
    {
        var rooms = DungeonPathfinder.BuildRooms(graph, geo, hazards);
        var entered = new HashSet<int>();
        var looked = new HashSet<int>();
        foreach (var p in route.Points)
        {
            log.TryGetValue(p, out var info);
            uint cell = info != null && info.Cell != 0 ? info.Cell : DungeonPathfinder.CellAt(graph, geo, p);
            if (!rooms.RoomOf.TryGetValue(cell, out int r)) continue;
            if (info != null && info.Why.StartsWith("look-in", StringComparison.Ordinal) && info.Room == r) looked.Add(r);
            else entered.Add(r);
        }
        int tiny = entered.Count(r => rooms.Cells[r].Count <= 5);
        var tinyList = entered.Where(r => rooms.Cells[r].Count <= 5).OrderBy(r => r)
            .Select(r => $"{r}[{string.Join(",", rooms.Cells[r].Select(c => (c & 0xFFFF).ToString("X4")))}]");
        return $"rooms entered {entered.Count} (of {rooms.Cells.Count}), of them <= 5 cells: {tiny}: {string.Join(" ", tinyList)}; looked into {looked.Count}";
    }

    private static string Ids(IEnumerable<uint> cells) => string.Join(",", cells.OrderBy(c => c).Select(c => (c & 0xFFFF).ToString("X4")));
}

using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset;
using DotRecast.Recast.Toolset.Builder;
using RynthNav.Baker;
using RynthNav.Routing;

internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static string _root = "";

    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "rnav-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Console.WriteLine("=== RynthNav route graph tests ===");
            Run(nameof(RegionsSplitIslands), RegionsSplitIslands);
            Run(nameof(PortalsMatchAcrossTheSeam), PortalsMatchAcrossTheSeam);
            Run(nameof(GapSplitsAStretchAndCostsFollowTheFloor), GapSplitsAStretchAndCostsFollowTheFloor);
            Run(nameof(AStarPicksTheCheaperWay), AStarPicksTheCheaperWay);
            Run(nameof(AStarNoRouteAndSameRegion), AStarNoRouteAndSameRegion);
            Run(nameof(FileRoundTripAndSetCheck), FileRoundTripAndSetCheck);
            Run(nameof(RouteFollower), RouteFollower);
            Run(nameof(EndToEndAroundAWall), EndToEndAroundAWall);
            Run(nameof(LandmassesAndSameLand), LandmassesAndSameLand);
            Run(nameof(PathGoesRoundAPortal), PathGoesRoundAPortal);
        }
        finally { try { Directory.Delete(_root, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} assertions)" : $"FAIL ({_fails} of {_asserts} assertions failed)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = _fails;
        try { test(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
    }

    // ── Baking made-up floors with the baker's Recast settings ───────────────────

    /// <summary>Flat rectangles (x0, z0, x1, z1) at height 0, plus a corner marker so the grid starts at (0,0).</summary>
    private static string Bake(string name, params (float x0, float z0, float x1, float z1)[] floors)
    {
        var verts = new List<float>(); var faces = new List<int>();
        void Quad(float x0, float z0, float x1, float z1, float y)
        {
            int b = verts.Count / 3;
            verts.AddRange(new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 });
            // Both windings: Recast keeps the upward-facing one as walkable.
            faces.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        foreach (var f in floors) Quad(f.x0, f.z0, f.x1, f.z1, 0);
        // Pin the bounds to the landblock grid (unused vertices, as the baker does).
        float maxX = floors.Max(f => f.x1), maxZ = floors.Max(f => f.z1);
        verts.AddRange(new[] { 0f, -1f, 0f, (float)Math.Ceiling(maxX / 192) * 192f, 1f, (float)Math.Ceiling(maxZ / 192) * 192f });

        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        geom.CalculateNormals();
        var settings = new RcNavMeshBuildSettings
        {
            cellSize = 0.5f, cellHeight = 0.2f, agentHeight = 2.0f, agentRadius = 2.0f, agentMaxClimb = 1.0f, agentMaxSlope = 48f,
            minRegionSize = 8, mergedRegionSize = 20, partitioning = (int)RcPartition.WATERSHED,
            filterLowHangingObstacles = true, filterLedgeSpans = true, filterWalkableLowHeightSpans = true,
            edgeMaxLen = 12f, edgeMaxError = 1.3f, vertsPerPoly = 6, detailSampleDist = 6f, detailSampleMaxError = 1f,
            tiled = true, tileSize = 384, keepInterResults = false, buildAll = true,
        };
        NavMeshBuildResult res = new TileNavMeshBuilder().Build(geom, settings);
        if (!res.Success || res.NavMesh == null) throw new InvalidOperationException("bake failed");
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < res.NavMesh.GetMaxTiles(); i++)
        {
            DtMeshTile? t = res.NavMesh.GetTile(i);
            if (t?.data?.header == null || t.data.header.polyCount == 0) continue;
            uint lb = (uint)((t.data.header.x << 8) | t.data.header.y);
            using var fs = File.Create(Path.Combine(dir, $"nav_{lb:X4}.tile"));
            using var bw = new BinaryWriter(fs);
            new DtMeshDataWriter().Write(bw, t.data, RcByteOrder.LITTLE_ENDIAN, false);
        }
        return dir;
    }

    private static DtMeshData ReadTile(string dir, uint lb)
    {
        using var fr = File.OpenRead(Path.Combine(dir, $"nav_{lb:X4}.tile"));
        using var br = new BinaryReader(fr);
        return new DtMeshDataReader().Read(br, 6);
    }

    /// <summary>Local region of the polygon under (x, z) in a single tile.</summary>
    private static int RegionAtPoint(DtMeshData md, int[] regions, float x, float z)
    {
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 2, maxPolys = 1 << 16 };
        nav.Init(ref prm, 6);
        nav.AddTile(md, 0, 0, out _);
        new DtNavMeshQuery(nav).FindNearestPoly(new RcVec3f(x, 0, z), new RcVec3f(4, 10, 4), new DtQueryDefaultFilter(), out long r, out _, out _);
        if (r == 0) return -1;
        nav.GetTileAndPolyByRefUnsafe(r, out _, out DtPoly p);
        return regions[p.index];
    }

    private static GraphBuild.Stats BuildGraph(string dir, out NavGraph g)
    {
        string path = Path.Combine(dir, NavGraph.FileName);
        var st = GraphBuild.Run(dir, path, _ => { }, TimeSpan.Zero);
        g = NavGraph.Load(path);
        return st;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    private static void RegionsSplitIslands()
    {
        // One tile, two floors 40 m apart: two regions, and the numbering is stable.
        string dir = Bake("islands", (10, 10, 70, 180), (110, 10, 180, 180));
        DtMeshData md = ReadTile(dir, 0x0000);
        int[] r = NavRegions.Build(md, out int count);
        Check(count == 2, $"two islands are two regions (got {count})");
        int a = RegionAtPoint(md, r, 40, 90), b = RegionAtPoint(md, r, 145, 90);
        Check(a >= 0 && b >= 0 && a != b, "each floor is its own region");
        Check(r[0] == 0, "region ids follow polygon order (polygon 0 is in region 0)");
        int[] again = NavRegions.Build(ReadTile(dir, 0x0000), out _);
        Check(again.SequenceEqual(r), "the same tile always gives the same numbering");
        var fp = NavRegions.Fingerprint(md);
        Check(fp.Polys == md.header.polyCount && fp.Verts == md.header.vertCount, "fingerprint = the header's counts");
    }

    private static void PortalsMatchAcrossTheSeam()
    {
        // Two 2-tile strips (x 0..384), z 10..60 and z 120..170: each crosses the seam at x = 192.
        string dir = Bake("strips", (0, 10, 384, 60), (0, 120, 384, 170));
        var st = BuildGraph(dir, out NavGraph g);
        Check(st.Tiles == 2, $"two tiles (got {st.Tiles})");
        Check(g.PortalCount == 2, $"one portal per strip (got {g.PortalCount})");
        DtMeshData m0 = ReadTile(dir, 0x0000), m1 = ReadTile(dir, 0x0100);
        int[] r0 = NavRegions.Build(m0, out _), r1 = NavRegions.Build(m1, out _);
        int s0 = g.Region(0x0000, RegionAtPoint(m0, r0, 100, 35)), s1 = g.Region(0x0100, RegionAtPoint(m1, r1, 300, 35));
        int n0 = g.Region(0x0000, RegionAtPoint(m0, r0, 100, 145)), n1 = g.Region(0x0100, RegionAtPoint(m1, r1, 300, 145));
        bool southLinked = false, northLinked = false, crossed = false;
        for (int p = 0; p < g.PortalCount; p++)
        {
            var (a, b) = g.PortalRegions(p);
            var pos = g.Portal(p);
            if ((a == s0 && b == s1) || (a == s1 && b == s0)) { southLinked = true; Check(pos.Ns > 10 && pos.Ns < 60 && Math.Abs(pos.Ew - 192) < 0.5f, $"south portal on the seam inside the strip ({pos.Ew:F1}, {pos.Ns:F1})"); }
            if ((a == n0 && b == n1) || (a == n1 && b == n0)) { northLinked = true; Check(pos.Ns > 120 && pos.Ns < 170, "north portal inside its strip"); }
            if ((a == s0 && b == n1) || (a == n0 && b == s1)) crossed = true;
        }
        Check(southLinked && northLinked, "each strip links to the same strip across the seam");
        Check(!crossed, "no portal joins one strip to the other");
    }

    private static void GapSplitsAStretchAndCostsFollowTheFloor()
    {
        // Tile 0: a full floor. Tile 1: a C open to the west (touches the seam at z 0..40 and
        // 150..192, joined by a bar along x 340..384). One region each side; two stretches.
        string dir = Bake("cshape", (0, 0, 192, 192), (192, 0, 384, 40), (192, 150, 384, 192), (340, 0, 384, 192));
        BuildGraph(dir, out NavGraph g);
        Check(g.PortalCount == 2, $"a 110 m gap on the seam makes two portals between the same two regions (got {g.PortalCount})");
        if (g.PortalCount != 2) return;
        var (a0, b0) = g.PortalRegions(0); var (a1, b1) = g.PortalRegions(1);
        Check(a0 == a1 && b0 == b1, "both portals join the same pair of regions");
        // Inside the C (tile 1) the walk between the two portals goes round by the bar.
        int cRegion = g.RegionLandblock(a0) == 0x0100 ? a0 : b0;
        int[] ps = g.RegionPortals(cRegion);
        var p0 = g.Portal(ps[0]); var p1 = g.Portal(ps[1]);
        float straight = MathF.Sqrt((p0.Ew - p1.Ew) * (p0.Ew - p1.Ew) + (p0.Ns - p1.Ns) * (p0.Ns - p1.Ns));
        // Route from the south arm to the north arm, both inside the C: the only way is round.
        var route = g.FindRoute(cRegion, 300, 20, cRegion, 300, 170, out _, out _);
        Check(route != null && route.Count == 0, "same region: nothing to cross");
        int open = g.RegionLandblock(a0) == 0x0000 ? a0 : b0;
        // From the open floor in tile 0 to the north arm: enter by the north portal, not round the C.
        route = g.FindRoute(open, 150, 170, cRegion, 300, 170, out float cost, out _);
        Check(route != null && route.Count == 1 && g.Portal(route[0]).Ns > 150, "enters the C by the near (north) arm");
        // From the south arm of the C to the open floor's north: cross at the south portal, walk the open floor.
        // Straight end legs would take the north arm (the wall of the C is invisible to them):
        route = g.FindRoute(cRegion, 300, 20, open, 150, 170, out cost, out _);
        Check(route != null && route.Count == 1, "a route either way");
        // Measured on the navmesh, the start leg round the C is long: it leaves by the south arm
        // (~108 to the south portal + ~156 across the open floor; the north arm would be ~400).
        NavCoarse.Probe? sp = NavCoarse.ProbeFile(g, dir, 300, 0, 20, 10), gp = NavCoarse.ProbeFile(g, dir, 150, 0, 170, 10);
        NavCoarse? walked = sp != null && gp != null ? NavCoarse.Plan(g, sp, gp, out _) : null;
        Check(walked != null && walked.Count == 1 && walked.Cost < 300, $"with walked end legs it leaves the C by the south arm (cost {walked?.Cost:F0})");
        Check(straight > 100, $"the two portals are far apart along the seam ({straight:F0})");
    }

    private static NavGraph HandGraph()
    {
        // Regions 0..4 in tiles 0x0000..0x0400 (one region each). Portals: 0-1, 1-2, 2-4 (the long
        // way, cheap), 1-3, 3-4 (the short way, but region 3 is costly to cross).
        var b = new NavGraph.Builder();
        for (uint i = 0; i < 5; i++) b.AddTile(i << 8, 10, 10, 10, 1);
        int p01 = b.AddPortal(192, 0, 96, 0, 1);
        int p12 = b.AddPortal(384, 0, 96, 1, 2);
        int p24 = b.AddPortal(576, 0, 300, 2, 4);
        int p13 = b.AddPortal(300, 0, 192, 1, 3);
        int p34 = b.AddPortal(480, 0, 250, 3, 4);
        b.SetRegionCosts(1, new[] { p01, p12, p13 }, new float[] { 0, 192, 150, 0, 0, 120, 0, 0, 0 });
        b.SetRegionCosts(2, new[] { p12, p24 }, new float[] { 0, 250, 0, 0 });
        b.SetRegionCosts(3, new[] { p13, p34 }, new float[] { 0, 2000, 0, 0 });   // a long climb round a ridge
        b.SetRegionCosts(0, new[] { p01 }, new float[] { 0 });
        b.SetRegionCosts(4, new[] { p24, p34 }, new float[] { 0, 60, 0, 0 });
        using var ms = new MemoryStream();
        b.Write(ms);
        ms.Position = 0;
        return NavGraph.Load(ms);
    }

    private static void AStarPicksTheCheaperWay()
    {
        NavGraph g = HandGraph();
        var route = g.FindRoute(0, 96, 96, 4, 600, 280, out float cost, out _);
        Check(route != null && route.SequenceEqual(new[] { 0, 1, 2 }), $"goes 0-1-2-4, not over the costly region 3 (got {string.Join(",", route ?? new())})");
        Check(cost > 192 + 250 && cost < 1500, $"cost adds the walks inside regions ({cost:F0})");
    }

    private static void AStarNoRouteAndSameRegion()
    {
        var b = new NavGraph.Builder();
        b.AddTile(0x0000, 1, 1, 1, 2);   // two islands, nothing between them
        b.AddTile(0x0100, 1, 1, 1, 1);
        int p = b.AddPortal(192, 0, 50, 0, 2);
        b.SetRegionCosts(0, new[] { p }, new float[] { 0 });
        b.SetRegionCosts(2, new[] { p }, new float[] { 0 });
        using var ms = new MemoryStream(); b.Write(ms); ms.Position = 0;
        NavGraph g = NavGraph.Load(ms);
        Check(g.FindRoute(1, 10, 10, 2, 300, 50, out _, out _) == null, "an island with no portal: no route");
        Check(g.FindRoute(0, 10, 10, 2, 300, 50, out _, out _) is { Count: 1 }, "the connected one: one crossing");
        Check(g.FindRoute(2, 300, 50, 2, 310, 60, out _, out _) is { Count: 0 }, "same region: empty route");
        Check(g.FindRoute(-1, 0, 0, 2, 0, 0, out _, out _) == null && g.FindRoute(0, 0, 0, 99, 0, 0, out _, out _) == null, "bad regions: no route");
    }

    private static void FileRoundTripAndSetCheck()
    {
        NavGraph g = HandGraph();
        Check(g.TileCount == 5 && g.RegionCount == 5 && g.PortalCount == 5, "counts survive the file");
        Check(g.Matches(0x0200, 10, 10, 10), "a matching tile matches");
        Check(!g.Matches(0x0200, 11, 10, 10) && !g.Matches(0x0900, 10, 10, 10), "another tile (or one the graph lacks) doesn't");
        Check(g.RegionLandblock(3) == 0x0300 && g.Region(0x0300, 0) == 3 && g.Region(0x0300, 1) == -1, "region <-> landblock");
        var ids = new List<NavGraph.TileInfo> { new(1, 2, 3, 4, 0, 1) };
        var ids2 = new List<NavGraph.TileInfo> { new(1, 2, 3, 5, 0, 1) };
        Check(!NavGraph.SetIdOf(ids).SequenceEqual(NavGraph.SetIdOf(ids2)), "set id changes with any tile's fingerprint");
        bool threw = false;
        try { NavGraph.Load(new MemoryStream(new byte[] { 1, 2, 3, 4 })); } catch (Exception) { threw = true; }
        Check(threw, "garbage is refused");
    }

    private static void RouteFollower()
    {
        NavGraph g = HandGraph();
        NavCoarse c = NavCoarse.Plan(g, 0, 96, 96, 4, 600, 280, out _)!;
        Check(c != null && c.Count == 3, "three crossings");
        var loaded = new HashSet<uint> { 0x0000, 0x0100 };
        var s = c!.Steer(lb => loaded.Contains(lb), out bool final);
        Check(!final && s.Ew == 192, "aims at the furthest loaded crossing (0-1)");
        loaded.Add(0x0200);
        s = c.Steer(lb => loaded.Contains(lb), out final);
        Check(!final && s.Ew == 384, "more tiles loaded: aims further (1-2)");
        double before = c.Remaining(96, 96);
        Check(c.Advance(1) && c.Crossed == 1, "standing in region 1: one crossing done");
        Check(c.Remaining(200, 96) < before, "what's left shrinks along the route");
        Check(!c.Advance(3), "region 3 isn't on this route: plan again");
        Check(c.Advance(-1), "between polygons for a moment: keep going");
        loaded.Add(0x0400);
        Check(c.Advance(4) && c.Crossed == 3, "in the goal's region");
        c.Steer(lb => loaded.Contains(lb), out final);
        Check(final, "then the goal itself is the aim");
        Check(c.Upcoming(3).Count() <= 3, "upcoming landblocks are limited");
    }

    private static void EndToEndAroundAWall()
    {
        // Three tiles in a row; the middle one is walkable only along its north edge, so a walk
        // from the south of tile 0 to the south of tile 2 must go north and back.
        string dir = Bake("wall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192));
        BuildGraph(dir, out NavGraph g);
        DtMeshData m0 = ReadTile(dir, 0x0000), m2 = ReadTile(dir, 0x0200);
        int r0 = g.Region(0x0000, RegionAtPoint(m0, NavRegions.Build(m0, out _), 100, 20));
        int r2 = g.Region(0x0200, RegionAtPoint(m2, NavRegions.Build(m2, out _), 480, 20));
        NavCoarse? c = NavCoarse.Plan(g, r0, 100, 20, r2, 480, 20, out _);
        Check(c != null && c.Count == 2, "two crossings, through the middle tile");
        var first = c!.Steer(lb => lb == 0x0000 || lb == 0x0100, out _);
        Check(first.Ns > 150, $"the first aim is the north passage, not straight east ({first.Ew:F0}, {first.Ns:F0})");
        Check(c.Cost > 2 * 130, $"the cost includes going north and back ({c.Cost:F0})");
        NavCoarse.Probe? s0 = NavCoarse.ProbeFile(g, dir, 100, 0, 20, 40), t2 = NavCoarse.ProbeFile(g, dir, 480, 0, 20, 40);
        Check(t2 != null && t2.Region == r2 && s0 != null && s0.Region == r0, "the ends' regions read straight from their tile files");
        NavCoarse? m = NavCoarse.Plan(g, s0!, t2!, out _);
        Check(m != null && m.Count == 2 && m.Cost >= c.Cost - 1, $"with walked end legs too ({m?.Cost:F0})");
    }

    private static void LandmassesAndSameLand()
    {
        // Two joined tiles (the mainland) and a tile two landblocks away (an island).
        string dir = Bake("land", (0, 0, 192, 192), (192, 0, 384, 192), (576, 0, 768, 192));
        BuildGraph(dir, out NavGraph g);
        Check(g.SameLand(0x0000, 0x0100) == null, "before BuildComponents nothing is known");
        g.BuildComponents();
        Check(g.SameLand(0x0000, 0x0100) == true, "joined tiles: the same landmass");
        Check(g.SameLand(0x0000, 0x0300) == false && g.SameLand(0x0300, 0x0100) == false, "the island is another landmass");
        Check(g.SameLand(0x0000, 0x0900) == null, "no tile: unknown (walks are allowed)");
        Check(g.LandblockComponents(0x0300).Length == 1 && g.ComponentSize(g.LandblockComponents(0x0300)[0]) >= 1, "the island's landmass");
        Check(NavGraph.LandblockAt(200, 10) == 0x0100 && NavGraph.LandblockAt(-1, 10) == uint.MaxValue, "landblock of a world point");
    }

    private static void PathGoesRoundAPortal()
    {
        // One open floor; a portal stands on the straight line between start and goal.
        string dir = Bake("portalfloor", (0, 0, 192, 192));
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 4, maxPolys = 1 << 16 };
        nav.Init(prm, 6);
        var md0 = ReadTile(dir, 0x0000);
        nav.AddTile(md0, 0, 0, out _);
        var q = new DtNavMeshQuery(nav);
        var plain = new DtQueryDefaultFilter();
        var sP = new RcVec3f(30, 0, 96); var gP = new RcVec3f(160, 0, 96);
        q.FindNearestPoly(sP, new RcVec3f(4, 10, 4), plain, out long sRef, out RcVec3f sPt, out _);
        q.FindNearestPoly(gP, new RcVec3f(4, 10, 4), plain, out long gRef, out RcVec3f gPt, out _);
        Check(sRef != 0 && gRef != 0, "start and goal on the floor");

        List<RcVec3f> Path(IDtQueryFilter f)
        {
            Span<long> polys = new long[256];
            q.FindPath(sRef, gRef, sPt, gPt, f, polys, out int pc, 256);
            Span<DtStraightPath> sp = new DtStraightPath[64];
            q.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, 64, 0);
            var pts = new List<RcVec3f>();
            for (int i = 0; i < spc; i++) pts.Add(sp[i].pos);
            if (pc == 0 || polys[pc - 1] != gRef) pts.Clear();
            return pts;
        }
        double Closest(List<RcVec3f> pts, double x, double z)
        {
            double best = double.MaxValue;
            for (int i = 1; i < pts.Count; i++) best = Math.Min(best, PortalAvoid.SegmentDistance(x, z, pts[i - 1].X, pts[i - 1].Z, pts[i].X, pts[i].Z));
            return best;
        }

        var straight = Path(plain);
        Check(straight.Count >= 2 && Closest(straight, 95, 96) < 1, "the plain path walks right over the portal's spot");

        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(95, 96, PortalAvoid.LiveRadius, "Unwanted Portal") }, Array.Empty<PortalAvoid.Spot>());
        var filter = new AvoidFilter(avoid) { AllowA = sRef, AllowB = gRef };
        var round = Path(filter);
        Check(round.Count >= 2, "with the portal avoided there is still a path");
        // On open ground one big polygon can hold the start, the portal and more: the plugin then
        // bends the path round the portal (only onto ground that's on the navmesh).
        var pts = round.ConvertAll(v => ((double)v.X, (double)v.Z));
        var bent = avoid.Bend(pts, (x, z) =>
        {
            q.FindNearestPoly(new RcVec3f((float)x, 0, (float)z), new RcVec3f(1.5f, 10, 1.5f), plain, out long r, out _, out _);
            return r != 0;
        });
        double gap = double.MaxValue;
        for (int i = 1; i < bent.Count; i++) gap = Math.Min(gap, PortalAvoid.SegmentDistance(95, 96, bent[i - 1].X, bent[i - 1].Y, bent[i].X, bent[i].Y));
        Console.WriteLine($"     {md0.header.polyCount} polys; plain path {straight.Count} corners, filtered {round.Count}, bent {bent.Count}: closest {gap:F2} u");
        Check(gap >= PortalAvoid.LiveRadius - 0.01, $"the path keeps out of the portal's circle (closest {gap:F2} u)");
        Check(bent.Count > straight.Count, "it bends round it");
        Check(Math.Abs(bent[^1].X - gP.X) < 1 && Math.Abs(bent[^1].Y - gP.Z) < 1, "and still ends at the goal");
        // Beside the floor's edge only one side is ground: the bend goes that way.
        var edge = new PortalAvoid();
        edge.Set(new[] { new PortalAvoid.Spot(95, 3, PortalAvoid.LiveRadius, "by the edge") }, Array.Empty<PortalAvoid.Spot>());
        var e = edge.Bend(new List<(double, double)> { (30, 3), (160, 3) }, (x, z) => z > 2.5);
        Check(e.Count == 3 && e[1].Y > 3, "next to the edge it bends onto the ground side");

        // The goal right beside an unwanted portal: the goal polygon nearest the spot outside the circle.
        q.FindNearestPoly(new RcVec3f(96, 0, 96), new RcVec3f(8, 10, 8), new AvoidFilter(avoid), out long nearRef, out RcVec3f nearPt, out _);
        double d = Math.Sqrt((nearPt.X - 95) * (nearPt.X - 95) + (nearPt.Z - 96) * (nearPt.Z - 96));
        Check(nearRef != 0 && d >= PortalAvoid.LiveRadius - 0.01, $"a goal beside the portal snaps to ground outside its circle ({d:F2} u away)");
    }
}

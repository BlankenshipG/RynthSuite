using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Portals a server added that the world data (portals.tsv, retail/ACE) doesn't have: Infinite
// Leaftide's and other custom servers' own portals (2026-10-29). Found two ways:
//   * "/rnav portals scan": every portal object the client knows around you is written down,
//     where it stands (the source end);
//   * taking a portal: the teleport that follows tells where it goes (the landing), so a scanned
//     portal becomes a route link the first time it is used, and an unscanned one is added then.
// Kept per server (the world name the client logged into), so one server's portals never route a
// character on another. Portals with both ends on the map are given to the route planner as links.
//
// Dungeons (2026-10-29, later): a portal standing inside a dungeon is kept too (its cell and
// landblock-local x, y, z), a landing inside a dungeon keeps where you landed, and the way you
// walked inside the dungeon from where you landed to the portal you left by is kept as a walk
// (DungeonWalk). Those make the dungeon a hub like the Town Network (TownNet.Merge): a map portal
// into it, the walk inside, and the exit portal out (onto the map, or into another learned
// dungeon). Saved in NavData\custom-portals.txt and custom-dungeon-walks.txt. No host calls: the
// offline tests compile this file.

internal sealed class CustomPortal
{
    public string Name = "";
    public uint Wcid;
    /// <summary>Where the portal stands: its cell, and map coordinates when on the map, else landblock-local X, Y, Z.</summary>
    public uint Cell;
    public bool OnMap;
    public double Ns, Ew;
    public double X, Y, Z;
    /// <summary>Where it lands: learned by taking it. DestKnown false = only scanned.</summary>
    public bool DestKnown, DestOnMap;
    public double DestNs, DestEw;
    public uint DestCell;
    /// <summary>A landing in a dungeon: where in it (landblock-local). DestHasPos false = not known (older saves).</summary>
    public bool DestHasPos;
    public double DestX, DestY, DestZ;
    public DateTime SeenUtc, LearnedUtc;

    /// <summary>Both ends on the map: the route planner can use it as a plain portal link.</summary>
    public bool Routable => OnMap && DestKnown && DestOnMap;

    /// <summary>Stands in a dungeon (OnMap false).</summary>
    public bool InDungeon => !OnMap;
    public uint Landblock => Cell >> 16;
    public uint DestLandblock => DestCell >> 16;

    public PortalLink ToLink() => new(Ns, Ew, DestNs, DestEw, Name);
}

/// <summary>
/// One walk inside a dungeon: from where a teleport set you down (or where the recording began) to
/// the portal you left by, as you walked it (loops cut out). Landblock-local coordinates.
/// </summary>
internal sealed class DungeonWalk
{
    public uint Landblock;
    public uint ArrivalCell;
    public double ArrivalX, ArrivalY, ArrivalZ;
    /// <summary>The walk began at a landing (a teleport), not somewhere in the middle (a login).</summary>
    public bool FromLanding;
    public string ExitName = "";
    public uint ExitCell;
    public double ExitX, ExitY, ExitZ;
    /// <summary>From the arrival to the exit portal's centre (x, y, z), at least two points.</summary>
    public double[] Points = Array.Empty<double>();   // x0,y0,z0, x1,y1,z1, ...
    public double Length;
    public DateTime LearnedUtc;

    public int Count => Points.Length / 3;
    public (double X, double Y, double Z) this[int i] => (Points[i * 3], Points[i * 3 + 1], Points[i * 3 + 2]);

    public static double PathLength(double[] pts)
    {
        double len = 0;
        for (int i = 3; i + 2 < pts.Length; i += 3)
        {
            double dx = pts[i] - pts[i - 3], dy = pts[i + 1] - pts[i - 2];
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }
}

/// <summary>What a scan did with one portal object.</summary>
internal enum CustomScanResult { Added, Updated, BuiltIn, OffMap }

/// <summary>What a portal taken told the store (2026-11-01): new, a landing that changed, or nothing new.</summary>
internal enum CustomLearnResult { New, LandingChanged, AlreadyKnown }

internal sealed class CustomPortalStore
{
    /// <summary>A portal object this close (world units) to a saved one of the same name is that one.</summary>
    public const double SameUnits = 8.0;
    /// <summary>In a dungeon (landblock-local metres, with a height check): the same portal.</summary>
    public const double SameDungeonUnits = 3.0;
    public const double SameDungeonHeight = 3.0;
    /// <summary>A portals.tsv portal this close to a scanned one already routes it.</summary>
    public const double BuiltInUnits = 12.0;
    public const int MaxPerServer = 2000;
    public const int MaxWalksPerServer = 1000;
    /// <summary>Two walks between the same arrival and exit: within this, the same pair (the shorter is kept).</summary>
    public const double SameWalkEndUnits = 3.0;

    private readonly Dictionary<string, List<CustomPortal>> _byServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DungeonWalk>> _walks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every change (the plugin saves, and rebuilds its route list, when it moves).</summary>
    public int Version { get; private set; }

    /// <summary>
    /// What the last Learned / LearnedInDungeon call did. AlreadyKnown: the portal and its landing were
    /// known (within LandingSameUnits / LandingSameMeters), and nothing was rewritten: taking a known
    /// portal used to say "learned" and rewrite the file and the route hub every time (2026-11-01).
    /// </summary>
    public CustomLearnResult LastLearn { get; private set; }

    /// <summary>A map landing this close (yards, NavCoords.Distance; landings scatter a little) is the same landing.</summary>
    public const double LandingSameUnits = 30.0;
    /// <summary>A dungeon landing this close (metres, same landblock) is the same landing.</summary>
    public const double LandingSameMeters = 6.0;

    public IReadOnlyList<CustomPortal> For(string server) =>
        _byServer.TryGetValue(Key(server), out var l) ? l : Array.Empty<CustomPortal>();

    public IReadOnlyList<DungeonWalk> WalksFor(string server) =>
        _walks.TryGetValue(Key(server), out var l) ? l : Array.Empty<DungeonWalk>();

    public IEnumerable<string> Servers
    {
        get
        {
            var all = new HashSet<string>(_byServer.Keys, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(_walks.Keys);
            return all;
        }
    }

    private static string Key(string server) => string.IsNullOrWhiteSpace(server) ? "?" : server.Trim();

    private List<CustomPortal> ListFor(string server)
    {
        string k = Key(server);
        if (!_byServer.TryGetValue(k, out var l)) _byServer[k] = l = new List<CustomPortal>();
        return l;
    }

    private List<DungeonWalk> WalkListFor(string server)
    {
        string k = Key(server);
        if (!_walks.TryGetValue(k, out var l)) _walks[k] = l = new List<DungeonWalk>();
        return l;
    }

    /// <summary>The saved portal of that name standing at (cell, map spot), or null. Map portals only.</summary>
    public CustomPortal? Find(string server, string name, uint cell, bool onMap, double ns, double ew)
    {
        foreach (CustomPortal p in For(server))
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (onMap && p.OnMap) { if (NavCoords.Distance(p.Ns, p.Ew, ns, ew) <= SameUnits) return p; }
            else if (!onMap && !p.OnMap && (p.Cell >> 16) == (cell >> 16)) return p;
        }
        return null;
    }

    /// <summary>The saved portal of that name standing in the dungeon of <paramref name="cell"/> at (x, y, z), or null.</summary>
    public CustomPortal? FindInDungeon(string server, string name, uint cell, double x, double y, double z)
    {
        foreach (CustomPortal p in For(server))
        {
            if (p.OnMap || p.Landblock != (cell >> 16) || !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (Flat(p.X, p.Y, x, y) <= SameDungeonUnits && Math.Abs(p.Z - z) <= SameDungeonHeight) return p;
        }
        return null;
    }

    private static double Flat(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>A portals.tsv portal at that spot (same name, or any within half the distance).</summary>
    public static bool IsBuiltIn(IReadOnlyList<PortalLink>? builtIn, string name, double ns, double ew)
    {
        if (builtIn == null) return false;
        foreach (PortalLink b in builtIn)
        {
            double d = NavCoords.Distance(b.SrcNs, b.SrcEw, ns, ew);
            if (d > BuiltInUnits) continue;
            if (d <= BuiltInUnits / 2) return true;
            if (b.Name.Length > 0 && name.Length > 0
                && (b.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(b.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
        }
        return false;
    }

    /// <summary>A portal object on the map seen by a scan: written down (or its sighting refreshed). A dungeon one: OffMap (see SawInDungeon).</summary>
    public CustomScanResult Saw(string server, string name, uint wcid, uint cell, bool onMap, double ns, double ew,
        IReadOnlyList<PortalLink>? builtIn, DateTime nowUtc)
    {
        name = Clean(name);
        if (!onMap) return CustomScanResult.OffMap;
        CustomPortal? p = Find(server, name, cell, onMap, ns, ew);
        if (p == null && IsBuiltIn(builtIn, name, ns, ew)) return CustomScanResult.BuiltIn;
        if (p != null)
        {
            p.SeenUtc = nowUtc;   // kept in memory; written with the next change
            bool changed = (wcid != 0 && p.Wcid != wcid) || p.Cell != cell
                           || Math.Abs(p.Ns - ns) > 0.0005 || Math.Abs(p.Ew - ew) > 0.0005;
            if (wcid != 0) p.Wcid = wcid;
            p.Cell = cell; p.Ns = ns; p.Ew = ew;   // the live object is exact; the first sighting may not be
            if (changed) Version++;
            return CustomScanResult.Updated;
        }
        var list = ListFor(server);
        if (list.Count >= MaxPerServer) list.RemoveAt(0);
        list.Add(new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = true, Ns = ns, Ew = ew, SeenUtc = nowUtc });
        Version++;
        return CustomScanResult.Added;
    }

    /// <summary>A portal object standing in a dungeon (landblock-local x, y, z): written down, or its sighting refreshed.</summary>
    public CustomScanResult SawInDungeon(string server, string name, uint wcid, uint cell, double x, double y, double z, DateTime nowUtc)
    {
        name = Clean(name);
        CustomPortal? p = FindInDungeon(server, name, cell, x, y, z);
        if (p != null)
        {
            p.SeenUtc = nowUtc;   // kept in memory; written with the next change
            bool changed = (wcid != 0 && p.Wcid != wcid) || p.Cell != cell
                           || Math.Abs(p.X - x) > 0.05 || Math.Abs(p.Y - y) > 0.05 || Math.Abs(p.Z - z) > 0.05;
            if (wcid != 0) p.Wcid = wcid;
            p.Cell = cell; p.X = x; p.Y = y; p.Z = z;
            if (changed) Version++;
            return CustomScanResult.Updated;
        }
        var list = ListFor(server);
        if (list.Count >= MaxPerServer) list.RemoveAt(0);
        list.Add(new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = false, X = x, Y = y, Z = z, SeenUtc = nowUtc });
        Version++;
        return CustomScanResult.Added;
    }

    /// <summary>
    /// The map portal you were standing at took you to (destOnMap, destNs, destEw / destCell). A portal
    /// the world data already routes is left alone (returns null); otherwise it is added if it
    /// wasn't scanned, and its landing set. Returns the portal, or null when nothing was learned.
    /// </summary>
    public CustomPortal? Learned(string server, string name, uint wcid, uint cell, double ns, double ew,
        bool destOnMap, double destNs, double destEw, uint destCell, IReadOnlyList<PortalLink>? builtIn, DateTime nowUtc)
        => Learned(server, name, wcid, cell, ns, ew, destOnMap, destNs, destEw, destCell, false, 0, 0, 0, builtIn, nowUtc);

    /// <summary>As above, with where in the dungeon a dungeon landing set you down (<paramref name="destHasPos"/>).</summary>
    public CustomPortal? Learned(string server, string name, uint wcid, uint cell, double ns, double ew,
        bool destOnMap, double destNs, double destEw, uint destCell, bool destHasPos, double destX, double destY, double destZ,
        IReadOnlyList<PortalLink>? builtIn, DateTime nowUtc)
    {
        name = Clean(name);
        CustomPortal? p = Find(server, name, cell, true, ns, ew);
        bool added = false;
        if (p == null)
        {
            // portals.tsv holds map-to-map portals only: a landing in a dungeon is never one of them.
            if (destOnMap && IsBuiltIn(builtIn, name, ns, ew)) { LastLearn = CustomLearnResult.AlreadyKnown; return null; }
            var list = ListFor(server);
            if (list.Count >= MaxPerServer) list.RemoveAt(0);
            p = new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = true, Ns = ns, Ew = ew, SeenUtc = nowUtc };
            list.Add(p);
            added = true;
        }
        SetLanding(p, added, destOnMap, destNs, destEw, destCell, destHasPos, destX, destY, destZ, nowUtc);
        return p;
    }

    /// <summary>The dungeon portal you were standing at (landblock-local x, y, z) took you there: added if new, landing set.</summary>
    public CustomPortal LearnedInDungeon(string server, string name, uint wcid, uint cell, double x, double y, double z,
        bool destOnMap, double destNs, double destEw, uint destCell, bool destHasPos, double destX, double destY, double destZ, DateTime nowUtc)
    {
        name = Clean(name);
        CustomPortal? p = FindInDungeon(server, name, cell, x, y, z);
        bool added = false;
        if (p == null)
        {
            var list = ListFor(server);
            if (list.Count >= MaxPerServer) list.RemoveAt(0);
            p = new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = false, X = x, Y = y, Z = z, SeenUtc = nowUtc };
            list.Add(p);
            added = true;
        }
        else if (wcid != 0) p.Wcid = wcid;
        SetLanding(p, added, destOnMap, destNs, destEw, destCell, destHasPos, destX, destY, destZ, nowUtc);
        return p;
    }

    private void SetLanding(CustomPortal p, bool added, bool destOnMap, double destNs, double destEw, uint destCell,
        bool destHasPos, double destX, double destY, double destZ, DateTime nowUtc)
    {
        p.SeenUtc = nowUtc;
        // Known already, landing and all: nothing to write (and nothing to tell the player).
        if (!added && SameLanding(p, destOnMap, destNs, destEw, destCell, destHasPos, destX, destY, destZ))
        {
            LastLearn = CustomLearnResult.AlreadyKnown;
            return;
        }
        LastLearn = added || !p.DestKnown ? CustomLearnResult.New : CustomLearnResult.LandingChanged;
        p.DestKnown = true;
        p.DestOnMap = destOnMap;
        p.DestNs = destOnMap ? Math.Round(destNs, 2) : 0;
        p.DestEw = destOnMap ? Math.Round(destEw, 2) : 0;
        p.DestCell = destCell;
        p.DestHasPos = !destOnMap && destHasPos;
        p.DestX = p.DestHasPos ? Math.Round(destX, 2) : 0;
        p.DestY = p.DestHasPos ? Math.Round(destY, 2) : 0;
        p.DestZ = p.DestHasPos ? Math.Round(destZ, 2) : 0;
        p.LearnedUtc = nowUtc;
        Version++;
    }

    /// <summary>The landing <paramref name="p"/> already has is this one (map: within LandingSameUnits; dungeon: same landblock, within LandingSameMeters).</summary>
    internal static bool SameLanding(CustomPortal p, bool destOnMap, double destNs, double destEw, uint destCell,
        bool destHasPos, double destX, double destY, double destZ)
    {
        if (!p.DestKnown || p.DestOnMap != destOnMap) return false;
        if (destOnMap) return NavCoords.Distance(p.DestNs, p.DestEw, destNs, destEw) <= LandingSameUnits;
        if ((p.DestCell >> 16) != (destCell >> 16)) return false;
        if (!destHasPos) return true;                 // nothing more precise to compare
        if (!p.DestHasPos) return false;              // the known one had no spot: this one adds it
        double dx = p.DestX - destX, dy = p.DestY - destY, dz = p.DestZ - destZ;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= LandingSameMeters;
    }

    /// <summary>
    /// A walk inside a dungeon to one of its portals: kept, unless a walk between the same arrival
    /// and exit is already kept that is no longer (the shorter way wins). False when it was refused
    /// (fewer than two points) or a shorter one was already kept.
    /// </summary>
    public bool AddWalk(string server, DungeonWalk w)
    {
        if (w.Count < 2) return false;
        w.Length = DungeonWalk.PathLength(w.Points);
        var list = WalkListFor(server);
        for (int i = 0; i < list.Count; i++)
        {
            DungeonWalk o = list[i];
            if (o.Landblock != w.Landblock) continue;
            if (Flat(o.ArrivalX, o.ArrivalY, w.ArrivalX, w.ArrivalY) > SameWalkEndUnits || Math.Abs(o.ArrivalZ - w.ArrivalZ) > SameDungeonHeight) continue;
            if (Flat(o.ExitX, o.ExitY, w.ExitX, w.ExitY) > SameWalkEndUnits || Math.Abs(o.ExitZ - w.ExitZ) > SameDungeonHeight) continue;
            if (o.Length <= w.Length && (o.FromLanding || !w.FromLanding)) return false;
            list[i] = w;
            Version++;
            return true;
        }
        if (list.Count >= MaxWalksPerServer) list.RemoveAt(0);
        list.Add(w);
        Version++;
        return true;
    }

    /// <summary>The route links for <paramref name="server"/>: its portals with both ends on the map.</summary>
    public List<PortalLink> Links(string server)
    {
        var links = new List<PortalLink>();
        foreach (CustomPortal p in For(server))
            if (p.Routable) links.Add(p.ToLink());
        return links;
    }

    /// <summary>Forgets this server's portals (and the walks to them) whose name contains <paramref name="name"/>. How many portals went.</summary>
    public int Forget(string server, string name)
    {
        string n = name.Trim();
        int gone = 0;
        if (_byServer.TryGetValue(Key(server), out var l)) gone = l.RemoveAll(p => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        int walks = _walks.TryGetValue(Key(server), out var w) ? w.RemoveAll(x => x.ExitName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) : 0;
        if (gone + walks > 0) Version++;
        return gone;
    }

    /// <summary>Forgets every portal and dungeon walk saved for <paramref name="server"/>. How many portals went.</summary>
    public int Clear(string server)
    {
        int n = 0, w = 0;
        if (_byServer.TryGetValue(Key(server), out var l)) { n = l.Count; l.Clear(); }
        if (_walks.TryGetValue(Key(server), out var wl)) { w = wl.Count; wl.Clear(); }
        if (n + w > 0) Version++;
        return n;
    }

    private static string Clean(string name)
    {
        string s = (name ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length == 0 ? "Portal" : s;
    }

    // ── custom-portals.txt ─────────────────────────────────────────────────
    // One portal per line, tab-separated:
    //   server, name, wcid, cell (hex), ns, ew, dest (map|dungeon|-), destNs, destEw, destCell (hex),
    //   seen (UTC, ISO), learned (UTC, ISO or -),
    //   where (map|dungeon), x, y, z (in a dungeon), destX, destY, destZ (a dungeon landing; "-" = not known)
    // Lines starting with '#' are comments. The last seven are newer: a line without them is a map
    // portal whose dungeon landing spot isn't known.

    public string Serialize()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("# RynthNav custom portals, per server (written by RynthNav; hand-edits OK)\n");
        foreach (var (server, list) in _byServer)
            foreach (CustomPortal p in list)
            {
                sb.Append(server).Append('\t').Append(p.Name).Append('\t')
                  .Append(p.Wcid.ToString(ci)).Append('\t').Append(p.Cell.ToString("X8", ci)).Append('\t')
                  .Append(p.Ns.ToString("0.###", ci)).Append('\t').Append(p.Ew.ToString("0.###", ci)).Append('\t')
                  .Append(!p.DestKnown ? "-" : p.DestOnMap ? "map" : "dungeon").Append('\t')
                  .Append(p.DestNs.ToString("0.##", ci)).Append('\t').Append(p.DestEw.ToString("0.##", ci)).Append('\t')
                  .Append(p.DestCell.ToString("X8", ci)).Append('\t')
                  .Append(Iso(p.SeenUtc)).Append('\t').Append(p.LearnedUtc == default ? "-" : Iso(p.LearnedUtc)).Append('\t')
                  .Append(p.OnMap ? "map" : "dungeon").Append('\t')
                  .Append(p.X.ToString("0.##", ci)).Append('\t').Append(p.Y.ToString("0.##", ci)).Append('\t').Append(p.Z.ToString("0.##", ci)).Append('\t');
                if (p.DestHasPos)
                    sb.Append(p.DestX.ToString("0.##", ci)).Append('\t').Append(p.DestY.ToString("0.##", ci)).Append('\t').Append(p.DestZ.ToString("0.##", ci));
                else sb.Append("-\t-\t-");
                sb.Append('\n');
            }
        return sb.ToString();
    }

    public static CustomPortalStore Parse(string text) => Parse(text, null);

    /// <summary>Reads custom-portals.txt and (when given) custom-dungeon-walks.txt.</summary>
    public static CustomPortalStore Parse(string? text, string? walksText)
    {
        var ci = CultureInfo.InvariantCulture;
        var store = new CustomPortalStore();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 7) continue;
            var p = new CustomPortal { Name = f[1].Trim() };
            if (p.Name.Length == 0) continue;
            uint.TryParse(f[2], NumberStyles.Integer, ci, out p.Wcid);
            uint.TryParse(f[3], NumberStyles.HexNumber, ci, out p.Cell);
            if (!double.TryParse(f[4], NumberStyles.Float, ci, out p.Ns) || !double.TryParse(f[5], NumberStyles.Float, ci, out p.Ew)) continue;
            p.OnMap = true;
            string dest = f[6].Trim().ToLowerInvariant();
            p.DestKnown = dest is "map" or "dungeon";
            p.DestOnMap = dest == "map";
            if (f.Length > 8 && p.DestOnMap
                && (!double.TryParse(f[7], NumberStyles.Float, ci, out p.DestNs) || !double.TryParse(f[8], NumberStyles.Float, ci, out p.DestEw)))
                p.DestKnown = p.DestOnMap = false;
            if (f.Length > 9) uint.TryParse(f[9], NumberStyles.HexNumber, ci, out p.DestCell);
            if (f.Length > 10) p.SeenUtc = ReadIso(f[10]);
            if (f.Length > 11) p.LearnedUtc = ReadIso(f[11]);
            if (f.Length > 15 && f[12].Trim().Equals("dungeon", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(f[13], NumberStyles.Float, ci, out p.X) || !double.TryParse(f[14], NumberStyles.Float, ci, out p.Y)) continue;
                double.TryParse(f[15], NumberStyles.Float, ci, out p.Z);
                p.OnMap = false;
            }
            if (f.Length > 18 && p.DestKnown && !p.DestOnMap
                && double.TryParse(f[16], NumberStyles.Float, ci, out p.DestX) && double.TryParse(f[17], NumberStyles.Float, ci, out p.DestY))
            {
                double.TryParse(f[18], NumberStyles.Float, ci, out p.DestZ);
                p.DestHasPos = true;
            }
            store.ListFor(f[0]).Add(p);
        }
        if (walksText != null) store.ParseWalks(walksText);
        return store;
    }

    // ── custom-dungeon-walks.txt ───────────────────────────────────────────
    // One walk per line, tab-separated:
    //   server, landblock (hex), arrival cell (hex), arrival x, y, z, from a landing (1|0),
    //   exit name, exit cell (hex), exit x, y, z, length, learned (UTC, ISO), points "x,y,z;x,y,z;..."

    public string SerializeWalks()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("# RynthNav walks inside dungeons, per server: from a landing to the portal taken (written by RynthNav)\n");
        foreach (var (server, list) in _walks)
            foreach (DungeonWalk w in list)
            {
                sb.Append(server).Append('\t').Append(w.Landblock.ToString("X4", ci)).Append('\t')
                  .Append(w.ArrivalCell.ToString("X8", ci)).Append('\t')
                  .Append(w.ArrivalX.ToString("0.##", ci)).Append('\t').Append(w.ArrivalY.ToString("0.##", ci)).Append('\t').Append(w.ArrivalZ.ToString("0.##", ci)).Append('\t')
                  .Append(w.FromLanding ? '1' : '0').Append('\t')
                  .Append(w.ExitName).Append('\t').Append(w.ExitCell.ToString("X8", ci)).Append('\t')
                  .Append(w.ExitX.ToString("0.##", ci)).Append('\t').Append(w.ExitY.ToString("0.##", ci)).Append('\t').Append(w.ExitZ.ToString("0.##", ci)).Append('\t')
                  .Append(w.Length.ToString("0.#", ci)).Append('\t').Append(Iso(w.LearnedUtc)).Append('\t');
                for (int i = 0; i < w.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    var (x, y, z) = w[i];
                    sb.Append(x.ToString("0.##", ci)).Append(',').Append(y.ToString("0.##", ci)).Append(',').Append(z.ToString("0.##", ci));
                }
                sb.Append('\n');
            }
        return sb.ToString();
    }

    private void ParseWalks(string text)
    {
        var ci = CultureInfo.InvariantCulture;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 15) continue;
            var w = new DungeonWalk { ExitName = f[7].Trim() };
            if (!uint.TryParse(f[1], NumberStyles.HexNumber, ci, out w.Landblock)) continue;
            uint.TryParse(f[2], NumberStyles.HexNumber, ci, out w.ArrivalCell);
            double.TryParse(f[3], NumberStyles.Float, ci, out w.ArrivalX);
            double.TryParse(f[4], NumberStyles.Float, ci, out w.ArrivalY);
            double.TryParse(f[5], NumberStyles.Float, ci, out w.ArrivalZ);
            w.FromLanding = f[6].Trim() == "1";
            uint.TryParse(f[8], NumberStyles.HexNumber, ci, out w.ExitCell);
            double.TryParse(f[9], NumberStyles.Float, ci, out w.ExitX);
            double.TryParse(f[10], NumberStyles.Float, ci, out w.ExitY);
            double.TryParse(f[11], NumberStyles.Float, ci, out w.ExitZ);
            w.LearnedUtc = ReadIso(f[13]);
            var pts = new List<double>();
            bool ok = true;
            foreach (string pt in f[14].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] c = pt.Split(',');
                if (c.Length < 3 || !double.TryParse(c[0], NumberStyles.Float, ci, out double x)
                    || !double.TryParse(c[1], NumberStyles.Float, ci, out double y) || !double.TryParse(c[2], NumberStyles.Float, ci, out double z))
                { ok = false; break; }
                pts.Add(x); pts.Add(y); pts.Add(z);
            }
            if (!ok || pts.Count < 6) continue;
            w.Points = pts.ToArray();
            w.Length = DungeonWalk.PathLength(w.Points);
            WalkListFor(f[0]).Add(w);
        }
    }

    private static string Iso(DateTime t) => t == default ? "-" : t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime ReadIso(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToUniversalTime() : default;
}

/// <summary>
/// The way walked inside a dungeon since the last landing, landblock-local, with loops cut out (coming
/// back within <see cref="LoopUnits"/> of an earlier point drops everything walked since). Host-free.
/// </summary>
internal sealed class DungeonTrail
{
    public const double StepUnits = 1.0;
    public const double LoopUnits = 1.5;
    public const double LoopHeight = 2.0;
    public const int MaxPoints = 4000;

    private readonly List<double> _pts = new();
    public uint Landblock { get; private set; }
    public uint StartCell { get; private set; }
    public bool FromLanding { get; private set; }
    /// <summary>Grew past MaxPoints: no longer kept (a walk this long isn't a route).</summary>
    public bool Overflow { get; private set; }
    public int Count => _pts.Count / 3;

    public void Clear() { _pts.Clear(); Landblock = 0; StartCell = 0; Overflow = false; FromLanding = false; }

    public void Start(uint cell, double x, double y, double z, bool fromLanding)
    {
        Clear();
        Landblock = cell >> 16; StartCell = cell; FromLanding = fromLanding;
        _pts.Add(x); _pts.Add(y); _pts.Add(z);
    }

    /// <summary>One position; true when the trail changed.</summary>
    public bool Add(double x, double y, double z)
    {
        if (Overflow || _pts.Count == 0) return false;
        int n = Count;
        double lx = _pts[^3], ly = _pts[^2];
        if (Math.Sqrt((lx - x) * (lx - x) + (ly - y) * (ly - y)) < StepUnits) return false;
        for (int j = 0; j < n - 3; j++)
        {
            double jx = _pts[j * 3], jy = _pts[j * 3 + 1], jz = _pts[j * 3 + 2];
            if (Math.Sqrt((jx - x) * (jx - x) + (jy - y) * (jy - y)) < LoopUnits && Math.Abs(jz - z) < LoopHeight)
            {
                _pts.RemoveRange((j + 1) * 3, _pts.Count - (j + 1) * 3);
                return true;
            }
        }
        if (n >= MaxPoints) { Overflow = true; return false; }
        _pts.Add(x); _pts.Add(y); _pts.Add(z);
        return true;
    }

    /// <summary>The trail plus the portal's centre as its last point (x, y, z triples).</summary>
    public double[] To(double x, double y, double z)
    {
        var a = new List<double>(_pts);
        // The last recorded point and the portal itself: the walker walks into its centre.
        if (a.Count >= 6 && Math.Sqrt((a[^3] - x) * (a[^3] - x) + (a[^2] - y) * (a[^2] - y)) < 0.3) a.RemoveRange(a.Count - 3, 3);
        a.Add(x); a.Add(y); a.Add(z);
        return a.ToArray();
    }

    public (double X, double Y, double Z) First => (_pts[0], _pts[1], _pts[2]);
}

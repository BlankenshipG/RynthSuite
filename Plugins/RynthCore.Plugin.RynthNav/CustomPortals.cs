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
// character on another. Only portals with both ends on the map are given to the route planner;
// a portal into a dungeon is kept (it's listed) but can't be walked out of. Saved in
// NavData\custom-portals.txt. No host calls: the offline tests compile this file.

internal sealed class CustomPortal
{
    public string Name = "";
    public uint Wcid;
    /// <summary>Where the portal stands (its cell, and map coordinates when on the map).</summary>
    public uint Cell;
    public bool OnMap;
    public double Ns, Ew;
    /// <summary>Where it lands: learned by taking it. DestKnown false = only scanned.</summary>
    public bool DestKnown, DestOnMap;
    public double DestNs, DestEw;
    public uint DestCell;
    public DateTime SeenUtc, LearnedUtc;

    /// <summary>Both ends on the map: the route planner can use it.</summary>
    public bool Routable => OnMap && DestKnown && DestOnMap;

    public PortalLink ToLink() => new(Ns, Ew, DestNs, DestEw, Name);
}

/// <summary>What a scan did with one portal object.</summary>
internal enum CustomScanResult { Added, Updated, BuiltIn, OffMap }

internal sealed class CustomPortalStore
{
    /// <summary>A portal object this close (world units) to a saved one of the same name is that one.</summary>
    public const double SameUnits = 8.0;
    /// <summary>A portals.tsv portal this close to a scanned one already routes it.</summary>
    public const double BuiltInUnits = 12.0;
    public const int MaxPerServer = 2000;

    private readonly Dictionary<string, List<CustomPortal>> _byServer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every change (the plugin saves, and rebuilds its route list, when it moves).</summary>
    public int Version { get; private set; }

    public IReadOnlyList<CustomPortal> For(string server) =>
        _byServer.TryGetValue(Key(server), out var l) ? l : Array.Empty<CustomPortal>();

    public IEnumerable<string> Servers => _byServer.Keys;

    private static string Key(string server) => string.IsNullOrWhiteSpace(server) ? "?" : server.Trim();

    private List<CustomPortal> ListFor(string server)
    {
        string k = Key(server);
        if (!_byServer.TryGetValue(k, out var l)) _byServer[k] = l = new List<CustomPortal>();
        return l;
    }

    /// <summary>The saved portal of that name standing at (cell, map spot), or null.</summary>
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

    /// <summary>A portal object seen by a scan: written down (or its sighting refreshed).</summary>
    public CustomScanResult Saw(string server, string name, uint wcid, uint cell, bool onMap, double ns, double ew,
        IReadOnlyList<PortalLink>? builtIn, DateTime nowUtc)
    {
        name = Clean(name);
        if (!onMap) return CustomScanResult.OffMap;
        CustomPortal? p = Find(server, name, cell, onMap, ns, ew);
        if (p == null && IsBuiltIn(builtIn, name, ns, ew)) return CustomScanResult.BuiltIn;
        if (p != null)
        {
            p.SeenUtc = nowUtc;
            if (wcid != 0) p.Wcid = wcid;
            p.Cell = cell; p.Ns = ns; p.Ew = ew;   // the live object is exact; the first sighting may not be
            Version++;
            return CustomScanResult.Updated;
        }
        var list = ListFor(server);
        if (list.Count >= MaxPerServer) list.RemoveAt(0);
        list.Add(new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = true, Ns = ns, Ew = ew, SeenUtc = nowUtc });
        Version++;
        return CustomScanResult.Added;
    }

    /// <summary>
    /// The portal you were standing at took you to (destOnMap, destNs, destEw / destCell). A portal
    /// the world data already routes is left alone (returns null); otherwise it is added if it
    /// wasn't scanned, and its landing set. Returns the portal, or null when nothing was learned.
    /// </summary>
    public CustomPortal? Learned(string server, string name, uint wcid, uint cell, double ns, double ew,
        bool destOnMap, double destNs, double destEw, uint destCell, IReadOnlyList<PortalLink>? builtIn, DateTime nowUtc)
    {
        name = Clean(name);
        CustomPortal? p = Find(server, name, cell, true, ns, ew);
        if (p == null)
        {
            if (IsBuiltIn(builtIn, name, ns, ew)) return null;
            var list = ListFor(server);
            if (list.Count >= MaxPerServer) list.RemoveAt(0);
            p = new CustomPortal { Name = name, Wcid = wcid, Cell = cell, OnMap = true, Ns = ns, Ew = ew, SeenUtc = nowUtc };
            list.Add(p);
        }
        p.DestKnown = true;
        p.DestOnMap = destOnMap;
        p.DestNs = destOnMap ? Math.Round(destNs, 2) : 0;
        p.DestEw = destOnMap ? Math.Round(destEw, 2) : 0;
        p.DestCell = destCell;
        p.LearnedUtc = nowUtc;
        Version++;
        return p;
    }

    /// <summary>The route links for <paramref name="server"/>: its portals with both ends on the map.</summary>
    public List<PortalLink> Links(string server)
    {
        var links = new List<PortalLink>();
        foreach (CustomPortal p in For(server))
            if (p.Routable) links.Add(p.ToLink());
        return links;
    }

    /// <summary>Forgets this server's portals whose name contains <paramref name="name"/>. How many went.</summary>
    public int Forget(string server, string name)
    {
        if (!_byServer.TryGetValue(Key(server), out var l)) return 0;
        int n = l.RemoveAll(p => p.Name.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Forgets every portal saved for <paramref name="server"/>. How many went.</summary>
    public int Clear(string server)
    {
        if (!_byServer.TryGetValue(Key(server), out var l) || l.Count == 0) return 0;
        int n = l.Count;
        l.Clear();
        Version++;
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
    //   seen (UTC, ISO), learned (UTC, ISO or -)
    // Lines starting with '#' are comments.

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
                  .Append(Iso(p.SeenUtc)).Append('\t').Append(p.LearnedUtc == default ? "-" : Iso(p.LearnedUtc)).Append('\n');
            }
        return sb.ToString();
    }

    public static CustomPortalStore Parse(string text)
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
            store.ListFor(f[0]).Add(p);
        }
        return store;
    }

    private static string Iso(DateTime t) => t == default ? "-" : t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime ReadIso(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToUniversalTime() : default;
}

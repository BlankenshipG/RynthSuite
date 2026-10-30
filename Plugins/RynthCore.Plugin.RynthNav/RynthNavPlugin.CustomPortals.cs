using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Custom-server portals (CustomPortals.cs): "/rnav portals scan" writes down the portal objects
// around you, taking a portal learns where it lands, and the route planner gets this server's
// portals with both ends on the map on top of portals.tsv (2026-10-29). Tick thread.
public sealed partial class RynthNavPlugin
{
    private const string CustomPortalsFile = "custom-portals.txt";
    /// <summary>The portal you took: the live portal object this close to where portal space began.</summary>
    private const double UsedPortalUnits = 10.0;

    private CustomPortalStore _custom = new();
    private volatile CustomPortalStore? _loadedCustom;
    private bool _customLoaded;
    private int _savedCustomVersion;

    /// <summary>The server the client logged into (its world name); "" before login.</summary>
    private string _server = "";

    // The route list: portals.tsv plus this server's custom portals, rebuilt when either changes.
    private List<PortalLink>? _routePortals;
    private (string Server, int Version, int BaseCount) _routePortalsKey;

    // The portal object nearest you as portal space began (learned from the teleport that follows).
    private bool _usedPortalSeen;
    private string _usedPortalName = "";
    private uint _usedPortalWcid, _usedPortalCell;
    private double _usedPortalNs, _usedPortalEw;

    private void LoadCustomPortals()
    {
        string path = NavFile(CustomPortalsFile);
        Task.Run(() =>
        {
            CustomPortalStore s = new();
            try { if (File.Exists(path)) s = CustomPortalStore.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Host.Log($"[RynthNav] {CustomPortalsFile}: {ex.Message}"); }
            _loadedCustom = s;
        });
    }

    /// <summary>Takes the loaded store once, and saves it when it changes. Tick thread.</summary>
    private void TickCustomPortals(bool force = false)
    {
        if (!_customLoaded)
        {
            if (_loadedCustom is not { } loaded) return;
            _custom = loaded; _customLoaded = true; _savedCustomVersion = _custom.Version;
            int n = 0; foreach (string s in _custom.Servers) n += _custom.For(s).Count;
            if (n > 0) Host.Log($"[RynthNav] custom portals: {n} saved ({string.Join(", ", _custom.Servers)})");
        }
        if (_custom.Version != _savedCustomVersion || force)
        {
            _savedCustomVersion = _custom.Version;
            WriteLater(CustomPortalsFile, _custom.Serialize());
        }
        RefreshCustomCounts();
    }

    /// <summary>The server key for the custom portals: the world name, or "" before it's known.</summary>
    private string CurrentServer()
    {
        if (_server.Length == 0 && Host.TryGetWorldName(out string world) && world.Trim().Length > 0) _server = world.Trim();
        return _server;
    }

    /// <summary>
    /// The portals the route planner uses: portals.tsv, plus the logged-in server's custom portals
    /// with both ends on the map. Null when portals.tsv hasn't been loaded.
    /// </summary>
    private List<PortalLink>? RoutePortals()
    {
        if (_portals == null) return null;
        string server = CurrentServer();
        var key = (server, _custom.Version, _portals.Count);
        if (_routePortals != null && key == _routePortalsKey) return _routePortals;
        List<PortalLink> custom = server.Length > 0 ? _custom.Links(server) : new List<PortalLink>();
        if (custom.Count == 0) _routePortals = _portals;
        else
        {
            _routePortals = new List<PortalLink>(_portals.Count + custom.Count);
            _routePortals.AddRange(_portals);
            _routePortals.AddRange(custom);
            Host.Log($"[RynthNav] route portals: {_portals.Count} from portals.tsv + {custom.Count} custom for {server}");
        }
        _routePortalsKey = key;
        return _routePortals;
    }

    /// <summary>A live object's map spot: true with its world position when it's a portal standing on the map.</summary>
    private bool LivePortalAt(uint id, out uint cell, out double wx, out double wy, out string name)
    {
        cell = 0; wx = wy = 0; name = "";
        if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypePortal) == 0) return false;
        if (!Host.TryGetObjectPosition(id, out cell, out float x, out float y, out _) || cell == 0) return false;
        wx = ((cell >> 24) & 0xFF) * 192.0 + x; wy = ((cell >> 16) & 0xFF) * 192.0 + y;
        Host.TryGetObjectName(id, out name);
        name ??= "";
        return true;
    }

    // ── /rnav portals scan ─────────────────────────────────────────────────

    /// <summary>Writes down every portal object the client knows around you, for this server.</summary>
    private void ScanCustomPortals(double radiusYards)
    {
        string server = CurrentServer();
        if (server.Length == 0) { Say("portal scan: not logged in yet (no server name)"); return; }
        if (!Host.HasGetLiveObjectIds) { Say("portal scan: this RynthCore engine can't list the objects around you"); return; }
        if (!_customLoaded) { Say("portal scan: the saved portals are still loading; try again in a moment"); return; }
        EnsurePortalsLoaded();
        var seen = new Dictionary<CustomScanResult, int>();
        var added = new List<string>();
        int portals = 0;
        DateTime now = DateTime.UtcNow;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!LivePortalAt(id, out uint cell, out double wx, out double wy, out string name)) continue;
            if (_hasPose && radiusYards > 0 && Math.Sqrt((wx - _wx) * (wx - _wx) + (wy - _wy) * (wy - _wy)) > radiusYards) continue;
            portals++;
            bool onMap = IsOnMap(cell);
            double ns = NavCoords.NsFromWorld(wy), ew = NavCoords.EwFromWorld(wx);
            Host.TryGetObjectWcid(id, out uint wcid);
            CustomScanResult r = _custom.Saw(server, name, wcid, cell, onMap, ns, ew, _portals, now);
            seen[r] = seen.TryGetValue(r, out int c) ? c + 1 : 1;
            if (r == CustomScanResult.Added) added.Add($"{name} {NavCoords.Fmt(ns, ew)}");
        }
        int Count(CustomScanResult r) => seen.TryGetValue(r, out int c) ? c : 0;
        int total = _custom.For(server).Count, routable = _custom.Links(server).Count;
        Say($"portal scan ({server}{(radiusYards > 0 ? $", {radiusYards:0} yd" : "")}): {portals} portal(s) seen — "
          + $"{Count(CustomScanResult.Added)} new, {Count(CustomScanResult.Updated)} already saved, "
          + $"{Count(CustomScanResult.BuiltIn)} already in the world data, {Count(CustomScanResult.OffMap)} in a dungeon (not mapped).");
        foreach (string a in added) Say("  new: " + a);
        if (Count(CustomScanResult.Added) > 0)
            Say("A new portal's landing is learned the first time you take it; then routes use it. "
              + $"This server: {total} saved, {routable} with a known landing.");
        Host.Log($"[RynthNav] portal scan {server}: seen {portals}, added {Count(CustomScanResult.Added)}, updated {Count(CustomScanResult.Updated)}, "
               + $"built-in {Count(CustomScanResult.BuiltIn)}, off map {Count(CustomScanResult.OffMap)}; {total} saved, {routable} routable");
    }

    // ── Learning a landing from the teleport ───────────────────────────────

    /// <summary>Portal space began: remember the live portal nearest where you stood (the one you took, if any).</summary>
    private void NoteUsedPortal(bool havePos, uint cell, double wx, double wy)
    {
        _usedPortalSeen = false;
        if (!havePos || !IsOnMap(cell) || !Host.HasGetLiveObjectIds) return;
        double best = UsedPortalUnits * UsedPortalUnits;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!LivePortalAt(id, out uint pc, out double px, out double py, out string name)) continue;
            double d2 = (px - wx) * (px - wx) + (py - wy) * (py - wy);
            if (d2 >= best) continue;
            best = d2;
            Host.TryGetObjectWcid(id, out uint wcid);
            _usedPortalSeen = true;
            _usedPortalName = name; _usedPortalWcid = wcid; _usedPortalCell = pc;
            _usedPortalNs = NavCoords.NsFromWorld(py); _usedPortalEw = NavCoords.EwFromWorld(px);
        }
    }

    /// <summary>A teleport that wasn't a recall: if it began at a portal, learn where that portal lands.</summary>
    private void LearnCustomPortal(RecallSpot landing)
    {
        if (!_usedPortalSeen) return;
        _usedPortalSeen = false;
        string server = CurrentServer();
        if (server.Length == 0 || !_customLoaded) return;
        EnsurePortalsLoaded();
        CustomPortal? p = _custom.Learned(server, _usedPortalName, _usedPortalWcid, _usedPortalCell, _usedPortalNs, _usedPortalEw,
            landing.OnMap, landing.Ns, landing.Ew, _cellId, _portals, DateTime.UtcNow);
        if (p == null) return;   // the world data routes it already
        string where = landing.OnMap ? $"{NavCoords.Fmt(landing.Ns, landing.Ew)} ({landing.Label})" : landing.Label;
        Host.Log($"[RynthNav] custom portal learned ({server}): {p.Name} at {NavCoords.Fmt(p.Ns, p.Ew)} → {where}");
        Say($"learned portal '{p.Name}' → {where}" + (p.Routable ? " — routes can use it now" : " (lands in a dungeon: kept, not routed)"));
    }

    // ── Commands ───────────────────────────────────────────────────────────

    /// <summary>/rnav portals on|off|scan [yd]|list|forget &lt;name&gt;|clear. Tick thread.</summary>
    private void CmdPortals(string rest)
    {
        string arg = rest.Trim();
        int sp = arg.IndexOf(' ');
        string sub = (sp < 0 ? arg : arg.Substring(0, sp)).ToLowerInvariant();
        string tail = sp < 0 ? "" : arg.Substring(sp + 1).Trim();
        string server = CurrentServer();
        switch (sub)
        {
            case "":
            case "on":
            case "off":
                _portalsEnabled = sub != "off";
                NavDataConfig.WriteSwitch("portals", _portalsEnabled);
                Say($"portal and recall routing {(_portalsEnabled ? "ON" : "OFF")} for walking (go/goto)"
                  + (server.Length > 0 ? $"; {server}: {_custom.Links(server).Count} custom portal(s) in routes" : ""));
                break;
            case "learn":
            {
                string l = tail.ToLowerInvariant();
                if (l is "" or "start" or "on") StartLearn();
                else if (l is "stop" or "off") { if (LearningPortals) StopLearn("stopped", stopWalk: true); else Say("not learning portals"); }
                else if (l.StartsWith("range", StringComparison.Ordinal))
                {
                    string n = l.Substring(5).Trim();
                    if (n.Length == 0) { Say($"learn range {_learnRange:0} yd ({LearnRangeMin:0}-{LearnRangeMax:0})"); break; }
                    if (!double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                    { Say($"portals learn range <yards {LearnRangeMin:0}-{LearnRangeMax:0}>"); break; }
                    SetLearnRange(r);
                }
                else Say("portals learn | learn stop | learn range <yd> — try every portal in range, learn where each lands, come back, repeat");
                break;
            }
            case "scan":
                double yd = 0;
                if (tail.Length > 0 && (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out yd) || yd < 5 || yd > 1000))
                { Say("portals scan [yards 5-1000] — without a distance, every portal the client knows around you"); return; }
                ScanCustomPortals(yd);
                break;
            case "list":
            {
                if (server.Length == 0) { Say("not logged in yet (no server name)"); return; }
                IReadOnlyList<CustomPortal> list = _custom.For(server);
                if (list.Count == 0) { Say($"no custom portals saved for {server} — /rnav portals scan"); return; }
                Say($"custom portals for {server}: {list.Count} ({_custom.Links(server).Count} used by routes)");
                int shown = 0;
                foreach (CustomPortal p in list)
                {
                    if (++shown > 40) { Say($"  ... and {list.Count - 40} more ({CustomPortalsFile} in {NavDataDir})"); break; }
                    string dest = !p.DestKnown ? "landing not learned yet (take it once)"
                        : p.DestOnMap ? "→ " + NavCoords.Fmt(p.DestNs, p.DestEw) : $"→ a dungeon (landblock {p.DestCell >> 16:X4})";
                    Say($"  {p.Name} {NavCoords.Fmt(p.Ns, p.Ew)} {dest}");
                }
                break;
            }
            case "forget":
                if (server.Length == 0 || tail.Length == 0) { Say("portals forget <name> — forgets this server's custom portals with that in their name"); return; }
                Say($"forgot {_custom.Forget(server, tail)} custom portal(s) named like '{tail}' on {server}");
                break;
            case "clear":
                if (server.Length == 0) { Say("not logged in yet (no server name)"); return; }
                if (!tail.Equals("confirm", StringComparison.OrdinalIgnoreCase))
                { Say($"portals clear confirm — forgets all {_custom.For(server).Count} custom portal(s) saved for {server}"); return; }
                Say($"forgot all {_custom.Clear(server)} custom portal(s) for {server}");
                break;
            default:
                Say("portals on|off | scan [yd] | learn [stop | range <yd>] | list | forget <name> | clear confirm — custom portals are kept per server");
                break;
        }
    }
}

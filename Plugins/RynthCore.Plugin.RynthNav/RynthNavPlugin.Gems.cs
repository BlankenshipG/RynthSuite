using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthNav;

// Portal gems in the pack as route steps (PackGems.cs, 2026-10-29). Every couple of seconds the pack
// is looked through for teleport items (gems, recall stones, a custom server's town gems); the route
// planner gets them as recalls ("/rnav gems on|off"), and a route's gem step uses one (UseObject)
// and waits for the teleport like a recall. A gem used up just before a teleport (by you or by a
// route) teaches where it lands, kept per server in NavData\gems.txt. Tick thread.
public sealed partial class RynthNavPlugin
{
    private const string GemsFile = "gems.txt";
    private const uint StypeStackSize = 12, DidSpell = 28;
    private const long GemScanMs = 2000;
    private const long GemSpentWindowMs = 20000;   // used up this long before a teleport: that gem took you
    private const long GemUseRetryMs = 15000;

    private GemMemory _gemMemory = new();
    private volatile GemMemory? _loadedGems;
    private bool _gemsLoaded;
    private int _savedGemVersion;
    private volatile bool _gemsEnabled = true;
    private double _gemCostUnits = GemPlanner.GemCostUnitsDefault;

    private volatile List<PackGem> _packGems = new();
    private Dictionary<uint, (string Name, int Count)> _packSnap = new();   // by weenie class: stacks merged or split aren't used up
    private long _nextGemScanMs;

    // A gem just used up (seen gone from the pack), or used by a route's gem step.
    private uint _gemSpentWcid;
    private string _gemSpentName = "";
    private long _gemSpentMs;

    // The route's gem step.
    private long _gemLastUseMs;

    private void InitGems()
    {
        _gemsEnabled = NavDataConfig.ReadSwitch("gems", true);
        _gemCostUnits = Math.Clamp(NavDataConfig.ReadNumber("gemCostYards", GemPlanner.GemCostUnitsDefault), 0, 5000);
        string path = NavFile(GemsFile);
        Task.Run(() =>
        {
            GemMemory m = new();
            try { if (File.Exists(path)) m = GemMemory.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Host.Log($"[RynthNav] {GemsFile}: {ex.Message}"); }
            _loadedGems = m;
        });
    }

    /// <summary>Takes the loaded gem landings, saves them when they change, and looks through the pack every couple of seconds.</summary>
    private void TickGems(long now, bool force = false)
    {
        if (!_gemsLoaded && _loadedGems is { } loaded) { _gemMemory = loaded; _gemsLoaded = true; _savedGemVersion = loaded.Version; }
        if (_gemsLoaded && (_gemMemory.Version != _savedGemVersion || force))
        {
            _savedGemVersion = _gemMemory.Version;
            WriteLater(GemsFile, _gemMemory.Serialize());
        }
        if (force || now < _nextGemScanMs || !_hasPose) return;
        _nextGemScanMs = now + GemScanMs;
        if (_trkPortaling || (Host.HasIsPortaling && Host.IsPortaling())) return;   // the pack isn't changing hands mid-teleport
        ScanPackGems(now);
    }

    /// <summary>The teleport items in your pack (main pack and side packs), and which went missing since the last look.</summary>
    private void ScanPackGems(long now)
    {
        if (!Host.HasGetLiveObjectIds || !Host.HasGetObjectOwnershipInfo) return;
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me == 0) return;
        string server = CurrentServer();
        Atlas atlas = _atlas;
        var byWcid = new Dictionary<uint, PackGem>();
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _) || wielder == me) continue;
            bool mine = container == me
                || (container != 0 && Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == me);
            if (!mine) continue;
            int spell = Host.HasGetObjectDataIdProperty && Host.TryGetObjectDataIdProperty(id, DidSpell, out uint sp) && sp < 0x10000 ? (int)sp : 0;
            Host.TryGetObjectWcid(id, out uint wcid);
            if (spell == 0 && _gemMemory.Get(server, wcid) == null) continue;
            Host.TryGetItemType(id, out uint type);
            Host.TryGetObjectName(id, out string name);
            name ??= "";
            if (!GemPlanner.LooksLikeTeleportItem(type, spell, name, atlas, _gemMemory, server, wcid)) continue;
            int count = Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(id, StypeStackSize, out int st) && st > 0 ? st : 1;
            if (byWcid.TryGetValue(wcid, out PackGem? g)) g.Count += count;
            else byWcid[wcid] = new PackGem { Id = id, Wcid = wcid, Name = name, SpellId = spell, Count = count };
        }
        // One gone, or a stack down: used up (or sold, dropped, given: only a teleport soon after counts).
        var snap = new Dictionary<uint, (string Name, int Count)>();
        foreach (var (w, g) in byWcid) snap[w] = (g.Name, g.Count);
        foreach (var (w, was) in _packSnap)
        {
            int left = snap.TryGetValue(w, out var cur) ? cur.Count : 0;
            if (left >= was.Count) continue;
            _gemSpentWcid = w; _gemSpentName = was.Name; _gemSpentMs = now;
            Host.Log($"[RynthNav] gem: a {was.Name} left the pack ({left} left)");
        }
        _packSnap = snap;
        _packGems = new List<PackGem>(byWcid.Values);
    }

    /// <summary>The recalls the planner may use: spells and commands (RecallPlanner), plus the pack's gems.</summary>
    private List<RecallOption> AllRecalls(Atlas atlas, List<string>? notes)
    {
        List<RecallOption> list = RecallPlanner.Build(atlas, _known, _memory, _charKey, notes, ArrivalFor);
        if (!_gemsEnabled) { if (_packGems.Count > 0) notes?.Add("Portal gems are off (/rnav gems on)."); return list; }
        list.AddRange(GemPlanner.Build(_packGems, atlas, _memory, _charKey, _gemMemory, CurrentServer(), _gemCostUnits, notes, ArrivalFor));
        return list;
    }

    /// <summary>
    /// A landing in a dungeon RynthNav learned walks in: that arrival of the route hub (_townNet), so a
    /// recall or gem into it goes on by the dungeon's portals. By position when known (within 3 m),
    /// else by the exact cell. -1: not a learned arrival.
    /// </summary>
    private int ArrivalFor(uint cell, bool hasPos, double x, double y, double z)
    {
        TownNet? t = _townNet;
        if (t == null || cell == 0) return -1;
        int first = _baseTownNet?.Arrivals.Count ?? 0;
        if (hasPos)
        {
            int a = t.LearnedArrival(first, cell >> 16, x, y, z);
            if (a >= 0) return a;
        }
        return t.LearnedArrivalByCell(first, cell);
    }

    /// <summary>
    /// A teleport right after a gem was used up (or a route's gem step used one): that gem lands
    /// here. True when it was a gem's teleport (it is then nothing else: not a portal, not a recall).
    /// </summary>
    private bool TryLearnGem(RecallSpot landing, long now)
    {
        if (_gemSpentWcid == 0 || now - _gemSpentMs > GemSpentWindowMs) return false;
        // A recall RynthNav cast, or one announced: that's what this teleport was.
        if ((_castRecallSpell != 0 && now - _castRecallMs < RecallCastWindowMs) || _pendingRecallSlot != null) return false;
        uint wcid = _gemSpentWcid;
        string name = _gemSpentName;
        _gemSpentWcid = 0;
        string server = CurrentServer();
        if (server.Length == 0 || !_gemsLoaded) return true;
        RecallSpot? was = _gemMemory.Get(server, wcid);
        _gemMemory.Set(server, wcid, name, landing);
        string where = landing.OnMap ? $"{NavCoords.Fmt(landing.Ns, landing.Ew)} ({landing.Label})" : landing.Label;
        Host.Log($"[RynthNav] gem {name} (wcid {wcid}) lands at {where}{(was == null ? " (new)" : "")}");
        if (was == null || was.OnMap != landing.OnMap || NavCoords.Distance(was.Ns, was.Ew, landing.Ns, landing.Ew) > RecallNearUnits)
            Say($"learned: {name} takes you to {where}" + (landing.OnMap ? (_gemsEnabled ? " — routes can use it now" : " (gems are off in routes: /rnav gems on)") : " (a dungeon: not used for routes)"));
        return true;
    }

    /// <summary>
    /// One tick of a route's gem step (from StepRecall): uses one of that gem from the pack, again
    /// after a while if nothing happened. False when there's none left (the goto stops).
    /// </summary>
    private bool UseRouteGem(RecallOption o, long now)
    {
        if (_gemLastUseMs != 0 && now - _gemLastUseMs < GemUseRetryMs) return true;
        PackGem? g = null;
        foreach (PackGem p in _packGems) if (p.Wcid == o.ItemWcid && p.Count > 0) { g = p; break; }
        if (g == null || !Host.HasUseObject) return false;
        Host.UseObject(g.Id);
        _gemLastUseMs = now;
        _gemSpentWcid = g.Wcid; _gemSpentName = g.Name; _gemSpentMs = now;   // the teleport that follows is this gem's
        Host.Log($"[RynthNav] used {g.Name} (0x{g.Id:X8}, {g.Count} in the pack)");
        lock (_gate) _status = $"using {g.Name}";
        return true;
    }

    /// <summary>/rnav gems [on|off|list|cost &lt;yd&gt;|forget &lt;name&gt;].</summary>
    private void CmdGems(string rest)
    {
        string arg = rest.Trim();
        int sp = arg.IndexOf(' ');
        string sub = (sp < 0 ? arg : arg.Substring(0, sp)).ToLowerInvariant();
        string tail = sp < 0 ? "" : arg.Substring(sp + 1).Trim();
        string server = CurrentServer();
        switch (sub)
        {
            case "on":
            case "off":
                _gemsEnabled = sub == "on";
                NavDataConfig.WriteSwitch("gems", _gemsEnabled);
                Say($"portal gems in routes {(_gemsEnabled ? "ON" : "OFF")}");
                return;
            case "cost":
                if (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out double yd) || yd < 0 || yd > 5000)
                { Say($"gems cost <yards 0-5000> — what spending a gem counts as on top of using it (now {_gemCostUnits:0} yd)"); return; }
                _gemCostUnits = Math.Round(yd);
                NavDataConfig.WriteNumber("gemCostYards", _gemCostUnits);
                Say($"a gem now counts as {_gemCostUnits:0} yd of walking on top of using it (lower: routes spend gems more readily)");
                return;
            case "forget":
            {
                int n = 0;
                foreach (var (w, name, _) in new List<(uint, string, RecallSpot)>(_gemMemory.For(server)))
                    if (tail.Length > 0 && name.IndexOf(tail, StringComparison.OrdinalIgnoreCase) >= 0 && _gemMemory.Forget(server, w)) n++;
                Say($"forgot where {n} gem kind(s) named like '{tail}' land on {server}");
                return;
            }
            case "":
            case "list":
            {
                ScanPackGems(NowMs);
                var notes = new List<string>();
                List<RecallOption> usable = GemPlanner.Build(_packGems, _atlas, _memory, _charKey, _gemMemory, server, _gemCostUnits, notes, ArrivalFor);
                Say($"portal gems in routes: {(_gemsEnabled ? "ON" : "OFF")} (cost {_gemCostUnits:0} yd each); {_packGems.Count} kind(s) in your pack, {usable.Count} with a known landing:");
                foreach (RecallOption o in usable) Say($"  {o.ItemName} x{o.ItemCount} → {o.Where}{(o.ToArrival >= 0 ? ", then out through its learned portals" : "")} ({o.Via})");
                foreach (string n in notes) Say("  " + n);
                return;
            }
            default:
                Say("gems [list] | on | off | cost <yd> | forget <name> — teleport gems in your pack as route steps");
                return;
        }
    }
}

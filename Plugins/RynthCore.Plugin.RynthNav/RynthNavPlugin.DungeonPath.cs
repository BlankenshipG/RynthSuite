using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// Walking inside a dungeon RynthNav has never walked (2026-10-29): RynthAi's dungeon pathfinder (the
// Dungeon Patrol's, from the client's cell dat; the "RynthAi.DungeonPath" interface) works out the
// way from where you land to the portal a route needs. TownNet.Merge plans those walks for the
// route hub, the hub walker falls back to it when you land away from every walk, and learning
// portals uses it to reach a dungeon's portals. Walking one and taking the portal records it as a
// learned walk (DungeonWalks), which is used from then on. Without RynthAi everything works as
// before. Tick thread.
public sealed partial class RynthNavPlugin
{
    private const int DunPathMaxPoints = 512;
    private const long DunPathRetryMs = 5000;

    private readonly Dictionary<string, DungeonWay?> _dunPathCache = new(StringComparer.Ordinal);
    private bool _dunPathAvailable;
    private long _dunPathCheckMs;
    private string _dunPathWhy = "";
    private bool _dunPathNoData;   // a lookup found the dats not ready (TownNet.Merge plans again later)

    /// <summary>RynthAi's dungeon pathfinder is there (looked up again every few seconds while it isn't).</summary>
    private unsafe bool DungeonPathAvailable()
    {
        long now = NowMs;
        if (_dunPathAvailable || now < _dunPathCheckMs) return _dunPathAvailable;
        _dunPathCheckMs = now + DunPathRetryMs;
        _dunPathAvailable = DungeonPathTable() != null;
        if (_dunPathAvailable) Host.Log("[RynthNav] RynthAi's dungeon pathfinder is available: routes can walk dungeon portals nobody walked to yet");
        return _dunPathAvailable;
    }

    private unsafe RynthAiDungeonPathApiV1* DungeonPathTable()
    {
        if (!Host.HasGetPluginInterface) { _dunPathWhy = "this RynthCore can't link plugins"; return null; }
        try
        {
            var t = (RynthAiDungeonPathApiV1*)Host.GetPluginInterface(RynthAiDungeonPathApiV1.PluginName, RynthAiDungeonPathApiV1.InterfaceName, RynthAiDungeonPathApiV1.InterfaceVersion);
            if (t == null || t->Version < 1 || t->StructSize < (uint)sizeof(RynthAiDungeonPathApiV1) || t->FindPath == null)
            { _dunPathWhy = "RynthAi isn't loaded (or is older than its dungeon path interface)"; return null; }
            return t;
        }
        catch { _dunPathWhy = "RynthAi's interface failed"; return null; }
    }

    /// <summary>
    /// A walk inside dungeon <paramref name="lb"/> between two landblock-local points (x,y,z triples,
    /// local), or null. Remembered per pair (the dats don't change). With RynthAi's v2 interface
    /// (2026-10-30) the walk may drop down a ledge or shaft up to the drop setting, and keeps out of
    /// the doorways of the locked doors this session found (RynthNavPlugin.Doors.cs). Tick thread.
    /// </summary>
    private unsafe DungeonWay? DungeonPath(uint lb, double ax, double ay, double az, double bx, double by, double bz)
    {
        lb &= 0xFFFF;
        List<(double X, double Y, double Z)> avoid = LockedDoorSpots(lb);
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{lb:X4}|{ax:0.#},{ay:0.#},{az:0}|{bx:0.#},{by:0.#},{bz:0}|d{_dropMeters:0.#}|a{avoid.Count}:{_lockedDoorVersion}");
        if (_dunPathCache.TryGetValue(key, out DungeonWay? cached)) return cached;
        RynthAiDungeonPathApiV1* t = DungeonPathTable();
        if (t == null) { _dunPathAvailable = false; return null; }   // not cached: RynthAi may come back
        double ox = ((lb >> 8) & 0xFF) * 192.0, oy = (lb & 0xFF) * 192.0;
        var buf = new double[DunPathMaxPoints * 3];
        var kinds = new int[DunPathMaxPoints];
        int count = 0, r;
        bool v2 = t->Version >= 2 && t->StructSize >= (uint)sizeof(RynthAiDungeonPathApiV1) && t->FindPath2 != null;
        if (v2)
        {
            var av = new double[Math.Max(1, avoid.Count * 3)];
            for (int i = 0; i < avoid.Count; i++) { av[i * 3] = ox + avoid[i].X; av[i * 3 + 1] = oy + avoid[i].Y; av[i * 3 + 2] = avoid[i].Z; }
            fixed (double* p = buf) fixed (int* k = kinds) fixed (double* a = av)
                r = t->FindPath2(lb, ox + ax, oy + ay, az, ox + bx, oy + by, bz, _dropMeters, a, avoid.Count, p, k, DunPathMaxPoints, &count);
        }
        else
        {
            fixed (double* p = buf)
                r = t->FindPath(lb, ox + ax, oy + ay, az, ox + bx, oy + by, bz, p, DunPathMaxPoints, &count);
        }
        DungeonWay? result = null;
        if (r == RynthAiDungeonPathApiV1.ResultOk && count >= 2)
        {
            var pts = new double[count * 3];
            bool[]? drops = null;
            for (int i = 0; i < count; i++)
            {
                pts[i * 3] = buf[i * 3] - ox;
                pts[i * 3 + 1] = buf[i * 3 + 1] - oy;
                pts[i * 3 + 2] = buf[i * 3 + 2];
                if (v2 && kinds[i] == RynthAiDungeonPathApiV1.KindDrop) (drops ??= new bool[count])[i] = true;
            }
            result = new DungeonWay(pts, drops);
        }
        else if (r == RynthAiDungeonPathApiV1.ResultNoData) { _dunPathNoData = true; return null; }   // the dats may not be loaded yet: ask again later
        if (_dunPathCache.Count > 4000) _dunPathCache.Clear();
        _dunPathCache[key] = result;
        return result;
    }

    /// <summary>The way from where you stand to (x, y, z) in this dungeon, as walker points, or null.</summary>
    private List<TownNetPoint>? DungeonWayHere(double x, double y, double z)
    {
        if (!DungeonPathAvailable()) return null;
        DungeonWay? dw = DungeonPath(CurrentLandblock, LocalX, LocalY, _wz, x, y, z);
        if (dw == null) return null;
        var way = new List<TownNetPoint>(dw.ToPoints(_cellId));
        if (way.Count > 0) way[0] = new TownNetPoint { Cell = _cellId, X = LocalX, Y = LocalY, Z = _wz, Drop = way[0].Drop };
        if (dw.HasDrop) Host.Log($"[RynthNav] dungeon walk drops down {dw.Drops!.Count(d => d)} ledge(s) on the way (drops up to {_dropMeters:0.#} m allowed: /rnav drops)");
        return way.Count >= 2 ? way : null;
    }
}

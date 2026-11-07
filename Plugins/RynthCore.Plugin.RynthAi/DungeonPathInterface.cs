using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.DungeonPath" interface (Shared/RynthAiDungeonPathApi.cs,
/// 2026-10-29): the Dungeon Patrol's pathfinder (DungeonPathfinder.NavigateTo on the client's cell
/// dat) for another plugin. RynthNav walks to a dungeon portal with it when it has no walk of its
/// own there. Runs on the plugin pump thread (the caller's OnTick), on the current plugin instance.
/// </summary>
internal static unsafe class DungeonPathInterface
{
    private static RynthAiDungeonPathApiV1* _table;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiDungeonPathApiV1.InterfaceName || version == 0 || version > RynthAiDungeonPathApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiDungeonPathApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiDungeonPathApiV1));
            t->Version = RynthAiDungeonPathApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiDungeonPathApiV1);
            t->FindPath = &FindPath;
            t->FindPath2 = &FindPath2;
            t->ObstacleAhead = &ObstacleAhead;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table (plugin shutdown). Callers resolve it every tick.</summary>
    public static void Release()
    {
        var t = _table;
        _table = null;
        if (t != null) NativeMemory.Free(t);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FindPath(uint landblock, double sx, double sy, double sz, double gx, double gy, double gz,
        double* points, int maxPoints, int* count)
    {
        try
        {
            if (count != null) *count = 0;
            RynthAiPlugin? p = PluginExports.Runtime.Plugin;
            if (p == null || points == null || maxPoints < 2 || count == null) return RynthAiDungeonPathApiV1.ResultNoData;
            return p.DungeonPathForPlugin(landblock, sx, sy, sz, gx, gy, gz, points, maxPoints, count);
        }
        catch
        {
            return RynthAiDungeonPathApiV1.ResultNoData;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObstacleAhead(uint landcell, double x, double y, double z, double headingDeg, double ahead, double maxHeight, double* topAbove)
    {
        try
        {
            if (topAbove != null) *topAbove = 0;
            RynthAiPlugin? p = PluginExports.Runtime.Plugin;
            if (p == null || topAbove == null) return RynthAiDungeonPathApiV1.ResultNoData;
            return p.ObstacleAheadForPlugin(landcell, x, y, z, headingDeg, ahead, maxHeight, out *topAbove);
        }
        catch
        {
            return RynthAiDungeonPathApiV1.ResultNoData;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FindPath2(uint landblock, double sx, double sy, double sz, double gx, double gy, double gz,
        double maxDrop, double* avoid, int avoidCount, double* points, int* kinds, int maxPoints, int* count)
    {
        try
        {
            if (count != null) *count = 0;
            RynthAiPlugin? p = PluginExports.Runtime.Plugin;
            if (p == null || points == null || maxPoints < 2 || count == null) return RynthAiDungeonPathApiV1.ResultNoData;
            if (avoid == null) avoidCount = 0;
            return p.DungeonPathForPlugin(landblock, sx, sy, sz, gx, gy, gz, points, maxPoints, count,
                Math.Max(0, maxDrop), avoid, Math.Clamp(avoidCount, 0, 64), kinds);
        }
        catch
        {
            return RynthAiDungeonPathApiV1.ResultNoData;
        }
    }
}

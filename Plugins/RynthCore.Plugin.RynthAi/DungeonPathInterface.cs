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
}
